using System.Globalization;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Graphs.Commands;
using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Graphs.Editor;

/// <summary>
///     Shows editable properties for the currently selected graph node.
/// </summary>
public sealed class GraphInspectorPanel : Widget
{
    private readonly Column _content;
    private readonly GraphEditorState _state;
    private readonly ThemeData _theme;

    // ── Widget lifecycle ──────────────────────────────────────────────────────

    private Size _size;

    // Guards against the self-rebuild loop: an inline edit (e.g. dragging a colour slider) commits
    // through the command stack, which fires GraphChanged/CompileChanged → Rebuild(). Rebuilding would
    // destroy the very slider being dragged. While true, Rebuild() is skipped — the live widgets
    // already reflect the change; the next genuine rebuild (selection change, etc.) refreshes them.
    private bool _suppressRebuild;

    public GraphInspectorPanel(GraphEditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;
        _content = new Column {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };

        _state.SelectionChanged += Rebuild;
        _state.GraphChanged += Rebuild;
        _state.CompileChanged += Rebuild; // refresh compile status text when a new build finishes
        Rebuild();
    }

    // ── Building blocks ───────────────────────────────────────────────────────

    private const float RowH = 28f;
    private const float LabelW = 88f;

    private void Add(Widget widget) => _content.Children.Add(widget);

    private void Gap(float height) => Add(new SizedBox(height: height));

    private static AdwButton ActionButton(string icon, string label, Action onPressed,
        bool destructive = false) =>
        new(label: label, onPressed: onPressed) {
            IconName = icon,
            Style = destructive ? AdwButtonStyle.Destructive : AdwButtonStyle.Regular,
            Compact = true,
        };

    private Label Text(string text, float size, Color color, bool oneLine = false)
    {
        var label = new Label(text: text, fontSize: size, color: color);
        if (oneLine)
        {
            label.MaxLines = 1;
            label.Overflow = TextOverflow.Ellipsis;
        }

        return label;
    }

    /// <summary>A section: small heading, hairline, then its rows.</summary>
    private void Section(string title)
    {
        Gap(14f);
        Add(
            new Label(
                text: title.ToUpperInvariant(),
                fontSize: _theme.FontSizeCaption,
                color: _theme.Hint
            ) {
                LetterSpacing = 0.6f,
            }
        );
        Gap(4f);
        Add(new AdwSeparator());
        Gap(4f);
    }

    /// <summary>Title, a category chip in the node's canvas colour, and the description.</summary>
    private void Header(string title, string? category, string? description)
    {
        Add(Text(text: title, size: _theme.FontSizeTitle, color: _theme.OnSurface));
        if (!string.IsNullOrWhiteSpace(category))
        {
            Gap(4f);
            Add(
                new Row {
                    MainAxisAlignment = MainAxisAlignment.Start,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = {
                        new DecoratedBox {
                            Fill = GraphCanvas.CategoryColor(category),
                            Radius = 3f,
                            Child = new SizedBox(width: 10f, height: 10f),
                        },
                        new SizedBox(6f),
                        Text(
                            text: category,
                            size: _theme.FontSizeCaption,
                            color: _theme.Hint,
                            oneLine: true
                        ),
                    },
                }
            );
        }

        if (string.IsNullOrWhiteSpace(description)) return;
        Gap(6f);
        Add(
            Text(
                text: description,
                size: _theme.FontSizeCaption,
                color: _theme.OnSurface.WithAlpha(0.7f)
            )
        );
    }

    /// <summary>
    ///     Runs an edit that comes from one of this panel's own controls without rebuilding the
    ///     panel under it: a rebuild replaces the very field being typed into or dragged, which drops
    ///     focus after every keystroke. The live control already shows the new value.
    /// </summary>
    private void CommitQuietly(Guid nodeId, PropertyDefinition def, GraphValue value)
    {
        var node = _state.Graph.FindNode(nodeId);
        if (node is null) return;
        var old = node.Properties.GetValueOrDefault(def.Id) ?? GraphValue.Null();
        _suppressRebuild = true;
        try
        {
            _state.Commands.Execute(
                new ChangeNodePropertyCommand(
                    nodeId: nodeId,
                    propertyKey: def.Id,
                    oldValue: old,
                    newValue: value
                )
            );
        }
        finally
        {
            _suppressRebuild = false;
        }
    }

    private static bool IsModified(GraphNode node, PropertyDefinition def) =>
        node.Properties.TryGetValue(key: def.Id, value: out var value) && !value.IsNull &&
        value.ToString() != def.DefaultValue.ToString();

    private static int Decimals(PropertyDefinition def) =>
        def.Type == GraphTypeRef.Int ? 0
        : def.Step is { } step and > 0f ? Math.Clamp(
            value: (int)Math.Ceiling(-Math.Log10(step) - 1e-6),
            min: 0,
            max: 6
        )
        : 3;

    private void Rebuild()
    {
        if (_suppressRebuild) return;
        _content.Children.Clear();
        if (_state.SelectedEdges.Count > 1) RebuildEdges();
        else if (_state.SelectedEdgeId is { } edgeId &&
                 _state.Graph.Edges.FirstOrDefault(e => e.Id == edgeId) is { } edge)
            RebuildEdge(edge);
        else if (_state.SelectedNodes.Count > 1) RebuildMultiple();
        else if (_state.PrimarySelection is { } id && _state.Graph.FindNode(id) is { } node)
            RebuildNode(node);
        else RebuildGraphSummary();
        MarkNeedsLayout();
    }

    private string NodeName(Guid id)
    {
        var node = _state.Graph.FindNode(id);
        return node is null
            ? "(missing)"
            : _state.Registry.GetNodeDefinition(node.DefinitionId)?.DisplayName ??
              node.DefinitionId;
    }

    private void RebuildEdge(GraphEdge edge)
    {
        Header(title: "Cable", category: null, description: null);
        Section("Route");
        foreach ((string side, var end) in new[] {
                     ("From", edge.From),
                     ("To", edge.To),
                 })
        {
            string? shown = GraphConnections.Resolve(
                graph: _state.Graph,
                registry: _state.Registry,
                endpoint: end
            )?.Pin.DisplayName;
            string pin = string.IsNullOrWhiteSpace(shown)
                ? end.PinId
                : shown; // unnamed pins fall back to their id
            Add(
                new SizedBox(
                    height: RowH,
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 44f,
                                child: Text(
                                    text: side,
                                    size: _theme.FontSizeBody,
                                    color: _theme.Hint
                                )
                            ),
                            new Expanded(
                                Text(
                                    text: $"{NodeName(end.NodeId)} · {pin}",
                                    size: _theme.FontSizeBody,
                                    color: _theme.OnSurface,
                                    oneLine: true
                                )
                            ),
                        },
                    }
                )
            );
        }

        Gap(8f);
        Add(
            new Row {
                MainAxisAlignment = MainAxisAlignment.Start,
                Children = {
                    ActionButton(
                        destructive: true,
                        label: "Disconnect",
                        icon: MaterialIcons.LinkOff,
                        onPressed: () => _state.Commands.Execute(new DeleteEdgeCommand(edge.Id))
                    ),
                },
            }
        );
        Gap(10f);
        Add(
            Text(
                text: "Drag the cable's input end to re-plug it. Alt-click a cable to cut it.",
                size: _theme.FontSizeCaption,
                color: _theme.Hint
            )
        );
    }

    private void RebuildEdges()
    {
        Header(title: $"{_state.SelectedEdges.Count} cables", category: null, description: null);
        Section("Selection");
        foreach (var edge in _state.Graph.Edges.Where(e => _state.IsEdgeSelected(e.Id)))
        {
            Add(
                new SizedBox(
                    height: 22f,
                    child: Text(
                        text: $"{NodeName(edge.From.NodeId)} → {NodeName(edge.To.NodeId)}",
                        size: _theme.FontSizeBody,
                        color: _theme.OnSurface,
                        oneLine: true
                    )
                )
            );
        }

        Gap(10f);
        Add(
            new Row {
                MainAxisAlignment = MainAxisAlignment.Start,
                Children = {
                    ActionButton(
                        destructive: true,
                        label: "Disconnect all",
                        icon: MaterialIcons.LinkOff,
                        onPressed: () => _state.Commands.Execute(
                            new GraphGesture(
                                _state.SelectedEdges
                                    .Select(id => (IGraphCommand)new DeleteEdgeCommand(id))
                                    .ToArray()
                            )
                        )
                    ),
                },
            }
        );
    }

    private void RebuildMultiple()
    {
        var ids = _state.SelectedNodes
            .Where(id => _state.Graph.EditorData.NodeLayouts.ContainsKey(id)).ToArray();
        Header(title: $"{_state.SelectedNodes.Count} nodes", category: null, description: null);
        Section("Selection");
        // The names, most recent last — the same order the selection was built in.
        foreach (var group in _state.Graph.Nodes.Where(n => _state.IsSelected(n.Id))
                     .GroupBy(n => NodeName(n.Id)))
        {
            Add(
                new SizedBox(
                    height: 22f,
                    child: Text(
                        text: group.Count() > 1 ? $"{group.Key}  ×{group.Count()}" : group.Key,
                        size: _theme.FontSizeBody,
                        color: _theme.OnSurface,
                        oneLine: true
                    )
                )
            );
        }

        bool collapse = ids.Any(id => !_state.Graph.EditorData.NodeLayouts[id].IsCollapsed);
        Gap(10f);
        Add(
            new Row {
                MainAxisAlignment = MainAxisAlignment.Start,
                Children = {
                    ActionButton(
                        label: collapse ? "Collapse" : "Expand",
                        icon: collapse ? Icons.UnfoldLess : Icons.UnfoldMore,
                        onPressed: () => _state.Commands.Execute(
                            new CollapseNodesCommand(nodes: ids, collapsed: collapse)
                        )
                    ),
                    new SizedBox(6f),
                    ActionButton(
                        destructive: true,
                        label: "Delete",
                        icon: Icons.Delete,
                        onPressed: () => _state.Commands.Execute(
                            new GraphGesture(
                                _state.SelectedNodes
                                    .Select(id => (IGraphCommand)new DeleteNodeCommand(id))
                                    .ToArray()
                            )
                        )
                    ),
                },
            }
        );
        Gap(10f);
        Add(
            Text(
                text: "Arrows nudge, Ctrl D duplicates, drag moves them together.",
                size: _theme.FontSizeCaption,
                color: _theme.Hint
            )
        );
    }

    private void RebuildNode(GraphNode node)
    {
        var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
        if (def is null)
        {
            Header(title: node.DefinitionId, category: null, description: null);
            Gap(6f);
            Add(
                Text(
                    text: $"Definition '{node.DefinitionId}' not found",
                    size: _theme.FontSizeBody,
                    color: _theme.Error
                )
            );
            return;
        }

        Header(title: def.DisplayName, category: def.Category, description: def.Description);

        // Everyday controls first; setup-only ones (the rows kept off the card) in their own group.
        var everyday = def.Properties.Where(p => !p.InspectorOnly).ToArray();
        var setup = def.Properties.Where(p => p.InspectorOnly).ToArray();
        if (everyday.Length > 0)
        {
            Section("Properties");
            foreach (var prop in everyday) PropertyRow(node: node, prop: prop);
        }

        if (setup.Length > 0)
        {
            Section("Setup");
            foreach (var prop in setup) PropertyRow(node: node, prop: prop);
        }

        var diags = _state.LastValidation?.Diagnostics.Where(d => d.NodeId == node.Id).ToList();
        if (diags is not { Count: > 0 }) return;
        Section("Problems");
        foreach (var d in diags) Diagnostic(d);
    }

    private void Diagnostic(GraphDiagnostic d)
    {
        var color = d.Severity switch {
            GraphDiagnosticSeverity.Error => _theme.Error,
            GraphDiagnosticSeverity.Warning => _theme.Accent,
            _ => _theme.OnSurface,
        };
        Add(Text(text: d.Message, size: _theme.FontSizeCaption, color: color));
        Gap(2f);
    }

    /// <summary>
    ///     One property: label (with its unit) on the left, the control on the right, and a reset
    ///     button that lights up once the value differs from the definition's default.
    /// </summary>
    private void PropertyRow(GraphNode node, PropertyDefinition prop)
    {
        var nodeId = node.Id;
        node.Properties.TryGetValue(key: prop.Id, value: out var current);
        var value = current is null || current.IsNull ? prop.DefaultValue : current;
        string name = string.IsNullOrEmpty(prop.Unit)
            ? prop.DisplayName
            : $"{prop.DisplayName} ({prop.Unit})";

        AdwButton reset = null!;
        reset = new AdwButton(
            onPressed: () =>
            {
                // A structural edit as far as this panel goes: rebuild so the control shows the default.
                var live = _state.Graph.FindNode(nodeId);
                if (live is null) return;
                _state.Commands.Execute(
                    new ChangeNodePropertyCommand(
                        nodeId: nodeId,
                        propertyKey: prop.Id,
                        oldValue: live.Properties.GetValueOrDefault(prop.Id) ?? GraphValue.Null(),
                        newValue: prop.DefaultValue
                    )
                );
            }
        ) {
            IconName = Icons.Undo,
            Style = AdwButtonStyle.Flat,
            Compact = true,
            Enabled = IsModified(node: node, def: prop),
        };

        void Edited(GraphValue next)
        {
            CommitQuietly(nodeId: nodeId, def: prop, value: next);
            var live = _state.Graph.FindNode(nodeId);
            bool modified = live is not null && IsModified(node: live, def: prop);
            if (reset.Enabled == modified) return;
            reset.Enabled = modified;
        }

        Widget? control = null;
        Widget? below = null; // editors too wide for the value column go under the label
        if (prop.Editor == "gradient" && prop.Type == GraphTypeRef.String)
        {
            below = new SizedBox(
                height: 46f,
                child: new AdwGradientEditor(
                    gradient: ParseGradient(value.IsNull ? "" : value.AsString()),
                    onChanged: g => Edited(GraphValue.FromString(SerializeGradient(g)))
                )
            );
        }
        else if (prop.Type == GraphTypeRef.Int && prop.EnumLabels is { Length: > 0 } labels)
        {
            control = new AdwDropDown(
                items: labels,
                selectedIndex: value.IsNull
                    ? 0
                    : Math.Clamp(value: value.AsInt(), min: 0, max: labels.Length - 1),
                onSelected: index => Edited(GraphValue.FromInt(index))
            ) { Compact = true };
        }
        else if (prop.Type == GraphTypeRef.Float || prop.Type == GraphTypeRef.Int)
        {
            bool integer = prop.Type == GraphTypeRef.Int;
            control =
                new AdwNumberInput(value.IsNull ? 0f : integer ? value.AsInt() : value.AsFloat()) {
                    Step = prop.Step ?? (integer ? 1f : 0.01f),
                    Decimals = Decimals(prop),
                    Min = prop.Min ?? float.NegativeInfinity,
                    Max = prop.Max ?? float.PositiveInfinity,
                    OnChanged = v => Edited(
                        integer
                            ? GraphValue.FromInt(
                                (int)Math.Clamp(
                                    value: Math.Round(v),
                                    min: int.MinValue,
                                    max: int.MaxValue
                                )
                            )
                            : GraphValue.FromFloat(v)
                    ),
                };
        }
        else if (prop.Type == GraphTypeRef.Bool)
        {
            control = new Row {
                MainAxisAlignment = MainAxisAlignment.Start,
                Children = {
                    new AdwCheckButton(
                        value: !value.IsNull && value.AsBool(),
                        onChanged: v => Edited(GraphValue.FromBool(v))
                    ),
                },
            };
        }
        else if (prop.Type == GraphTypeRef.String)
        {
            control = new AdwTextField(decoration: new AdwInputDecoration(prop.DisplayName)) {
                Text = value.IsNull ? "" : value.AsString(),
                OnChanged = text => Edited(GraphValue.FromString(text)),
            };
        }
        else if (prop.Type == GraphTypeRef.Color)
        {
            below = BuildColorEditor(
                nodeId: nodeId,
                propDef: prop,
                propKey: prop.Id,
                rgba: value.IsNull ? [0.8f, 0.8f, 0.8f, 1f] : value.AsFloat4()
            );
        }
        else
            control = Text(
                text: value.IsNull ? "—" : value.ToString() ?? "—",
                size: _theme.FontSizeCaption,
                color: _theme.Hint,
                oneLine: true
            );

        var label = Text(
            text: name,
            size: _theme.FontSizeBody,
            color: _theme.OnSurface,
            oneLine: true
        );
        if (below is not null)
        {
            Add(
                new SizedBox(
                    height: RowH,
                    child: new Row {
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new Expanded(label),
                            new SizedBox(width: 24f, height: 24f, child: reset),
                        },
                    }
                )
            );
            Add(below);
            Gap(4f);
            return;
        }

        Add(
            new SizedBox(
                height: RowH,
                child: new Row {
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = {
                        new SizedBox(width: LabelW, child: label),
                        new SizedBox(6f),
                        new Expanded(new SizedBox(height: 26f, child: control)),
                        new SizedBox(2f),
                        new SizedBox(width: 24f, height: 24f, child: reset),
                    },
                }
            )
        );
    }

    /// <summary>
    ///     Inline colour editor for a Color (Float4) property: a live swatch above three R/G/B sliders.
    ///     Each change commits through the undo stack and triggers a reactive recompile (live preview).
    /// </summary>
    private Widget BuildColorEditor(Guid nodeId, PropertyDefinition propDef, string propKey,
        float[] rgba)
    {
        float r = rgba[0];
        float g = rgba[1];
        float b = rgba[2];
        float a = rgba.Length > 3 ? rgba[3] : 1f;

        var swatch = new ColorSwatch(value: new Color(r: r, g: g, b: b), theme: _theme);

        void Commit()
        {
            swatch.Value = new Color(r: r, g: g, b: b);
            swatch.MarkNeedsPaint();
            var node = _state.Graph.FindNode(nodeId);
            var old = node is not null &&
                      node.Properties.TryGetValue(key: propKey, value: out var o)
                ? o
                : propDef.DefaultValue;
            // Suppress the rebuild this triggers so the slider being dragged is not torn down mid-drag.
            _suppressRebuild = true;
            try
            {
                _state.Commands.Execute(
                    new ChangeNodePropertyCommand(
                        nodeId: nodeId,
                        propertyKey: propKey,
                        oldValue: old,
                        newValue: GraphValue.FromFloat4(
                            x: r,
                            y: g,
                            z: b,
                            w: a
                        )
                    )
                );
            }
            finally
            {
                _suppressRebuild = false;
            }
        }

        Widget Channel(string label, float value, Action<float> set)
        {
            var slider = new AdwSlider(value) {
                Min = 0f,
                Max = 1f,
                OnChanged = v =>
                {
                    set(v);
                    Commit();
                },
            };
            return new SizedBox(
                height: 22f,
                child: new Row {
                    MainAxisAlignment = MainAxisAlignment.Start,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = {
                        new SizedBox(
                            width: 14f,
                            child: new Label(
                                text: label,
                                fontSize: _theme.FontSizeCaption,
                                color: _theme.Hint
                            )
                        ),
                        new SizedBox(6f),
                        new Expanded(slider),
                    },
                }
            );
        }

        return new Column {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children = {
                new SizedBox(height: 22f, child: swatch),
                new SizedBox(height: 4f),
                Channel(label: "R", value: r, set: v => r = v),
                Channel(label: "G", value: g, set: v => g = v),
                Channel(label: "B", value: b, set: v => b = v),
            },
        };
    }

    // ── Gradient (Color Ramp) property — flat "pos,r,g,b,a,…" string ↔ AdwColorGradient ─────────────

    private static AdwColorGradient ParseGradient(string s)
    {
        var stops = new List<AdwGradientStop>();
        if (!string.IsNullOrWhiteSpace(s))
        {
            string[] parts = s.Split(
                separator: ',',
                options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            for (int i = 0; i + 4 < parts.Length; i += 5)
            {
                stops.Add(
                    new AdwGradientStop(
                        position: P(parts[i]),
                        color: new Color(
                            r: P(parts[i + 1]),
                            g: P(parts[i + 2]),
                            b: P(parts[i + 3]),
                            a: P(parts[i + 4])
                        )
                    )
                );
            }
        }

        return stops.Count >= 2 ? new AdwColorGradient(stops) : new AdwColorGradient();

        static float P(string x)
        {
            return float.TryParse(
                s: x,
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out float v
            )
                ? v
                : 0f;
        }
    }

    private static string SerializeGradient(AdwColorGradient g)
    {
        var fields = new List<string>(g.Stops.Count * 5);
        foreach (var s in g.Stops)
        {
            fields.Add(C(s.Position));
            fields.Add(C(s.Color.R));
            fields.Add(C(s.Color.G));
            fields.Add(C(s.Color.B));
            fields.Add(C(s.Color.A));
        }

        return string.Join(separator: ',', values: fields);

        static string C(float v) => v.ToString(
            format: "0.####",
            provider: CultureInfo.InvariantCulture
        );
    }

    private void RebuildGraphSummary()
    {
        var g = _state.Graph;
        Header(
            title: g.Name.Length > 0 ? g.Name : "Untitled graph",
            category: null,
            description: null
        );
        Gap(4f);
        Add(
            Text(
                text: $"{g.Nodes.Count} nodes · {g.Edges.Count} cables",
                size: _theme.FontSizeBody,
                color: _theme.Hint
            )
        );

        var cr = _state.LastCompileResult;
        if (cr is not null)
        {
            Section("Build");
            Add(
                Text(
                    text: cr.Success ? "✓ Build succeeded" : "✗ Build failed",
                    size: _theme.FontSizeBody,
                    color: cr.Success ? _theme.Success : _theme.Error
                )
            );
            if (cr.CompiledArtifact is string { Length: > 0 } artifact)
            {
                Gap(6f);
                // Line by line, so each wraps on its own in the narrow panel.
                foreach (string line in artifact.Split(
                             separator: '\n',
                             options: StringSplitOptions.RemoveEmptyEntries
                         ))
                    Add(
                        Text(
                            text: line.TrimEnd(),
                            size: _theme.FontSizeCaption,
                            color: _theme.OnSurface
                        )
                    );
            }

            if (cr.Diagnostics.Count > 0)
            {
                Gap(6f);
                foreach (var d in cr.Diagnostics) Diagnostic(d);
            }
        }

        Section("Shortcuts");
        foreach ((string keys, string action) in new[] {
                     ("Shift A / Space", "Add a node"),
                     ("Shift-click", "Add or remove from selection"),
                     ("Drag on canvas", "Select a group"),
                     ("Arrows", "Nudge (Shift: fine)"),
                     ("Ctrl C / X / V", "Copy, cut, paste"),
                     ("Ctrl D", "Duplicate"),
                     ("Del", "Delete"),
                     ("H", "Collapse or expand"),
                     ("F", "Frame selection or all"),
                     ("Alt-click cable", "Cut it"),
                     ("Drop node on cable", "Insert it"),
                     ("Scroll / right-drag", "Pan"),
                     ("Ctrl scroll / wheel", "Zoom"),
                 })
        {
            Add(
                new SizedBox(
                    height: 20f,
                    child: new Row {
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: LabelW + 44f,
                                child: Text(
                                    text: keys,
                                    size: _theme.FontSizeCaption,
                                    color: _theme.OnSurface,
                                    oneLine: true
                                )
                            ),
                            new Expanded(
                                Text(
                                    text: action,
                                    size: _theme.FontSizeCaption,
                                    color: _theme.Hint,
                                    oneLine: true
                                )
                            ),
                        },
                    }
                )
            );
        }
    }

    public override Size Measure(Constraints c)
    {
        // Width follows the constraint; height sizes to content. This panel lives inside a vertical
        // ScrollView, where c.MaxHeight is ∞ — filling it would report an infinite scroll extent
        // (a drag then drives the offset to ∞, and the scrollbar computes ∞/∞ = NaN → paint crash).
        // A tight-width / loose-height measure lets the Column report its natural stacked height.
        float width = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
        var content = _content.Measure(
            new Constraints(
                minWidth: width,
                maxWidth: width,
                minHeight: 0f,
                maxHeight: c.MaxHeight
            )
        );
        _size = c.Constrain(new Size(width: width, height: content.Height));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _content.Layout(origin);
    }

    public override void Paint(PaintList paint) => _content.Paint(paint);

    public override Widget? HitTest(Offset point) => Bounds.Contains(px: point.X, py: point.Y)
        ? _content.HitTest(point) ?? this
        : null;

    public override IEnumerable<Widget> GetChildren() => [_content];

    /// <summary>A non-interactive colour preview bar for the inline colour editor.</summary>
    private sealed class ColorSwatch(Color value, ThemeData theme) : Widget
    {
        private Size _s;
        public Color Value { get; set; } = value;

        public override Size Measure(Constraints c)
        {
            _s = c.Constrain(new Size(width: c.MaxWidth, height: 22f));
            return _s;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _s.Width,
                height: _s.Height
            );
        }

        public override void Paint(PaintList paint)
        {
            paint.AddRect(bounds: Bounds, color: Value.WithAlpha(1f), radius: Radii.Sm);
            paint.AddBorder(bounds: Bounds, color: theme.Separator, radius: Radii.Sm);
        }
    }
}
