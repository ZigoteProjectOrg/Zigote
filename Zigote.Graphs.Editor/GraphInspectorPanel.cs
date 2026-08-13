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

    private void Rebuild()
    {
        if (_suppressRebuild) return;
        _content.Children.Clear();
        var selectedId = _state.PrimarySelection;
        if (selectedId is null)
        {
            RebuildGraphSummary();
            return;
        }

        var node = _state.Graph.FindNode(selectedId.Value);
        if (node is null) return;

        var def = _state.Registry.GetNodeDefinition(node.DefinitionId);

        // Header
        _content.Children.Add(
            new Label(
                text: def?.DisplayName ?? node.DefinitionId,
                fontSize: _theme.FontSizeTitle,
                color: _theme.OnSurface
            )
        );
        _content.Children.Add(new SizedBox(height: 4f));

        if (def is null)
        {
            _content.Children.Add(
                new Label(
                    text: $"Definition '{node.DefinitionId}' not found",
                    fontSize: _theme.FontSizeBody,
                    color: _theme.Error
                )
            );
            return;
        }

        // Category + description
        if (!string.IsNullOrWhiteSpace(def.Category))
        {
            _content.Children.Add(
                new Label(text: def.Category, fontSize: _theme.FontSizeCaption, color: _theme.Hint)
            );
        }

        if (!string.IsNullOrWhiteSpace(def.Description))
        {
            _content.Children.Add(new SizedBox(height: 4f));
            _content.Children.Add(
                new Label(
                    text: def.Description,
                    fontSize: _theme.FontSizeCaption,
                    color: _theme.OnSurface.WithAlpha(0.7f)
                )
            );
        }

        // Properties
        if (def.Properties.Count > 0)
        {
            _content.Children.Add(new SizedBox(height: 8f));
            _content.Children.Add(
                new Label(text: "Properties", fontSize: _theme.FontSizeCaption, color: _theme.Hint)
            );
            _content.Children.Add(new Divider());

            foreach (var prop in def.Properties)
            {
                var propDef = prop;
                var nodeId = node.Id;
                string propKey = prop.Id;
                node.Properties.TryGetValue(key: propKey, value: out var current);
                var value = current ?? prop.DefaultValue;

                _content.Children.Add(new SizedBox(height: 4f));
                _content.Children.Add(
                    new Label(
                        text: prop.DisplayName,
                        fontSize: _theme.FontSizeBody,
                        color: _theme.OnSurface
                    )
                );

                if (propDef.Editor == "gradient" && prop.Type == GraphTypeRef.String)
                {
                    var grad = ParseGradient(value.IsNull ? "" : value.AsString());
                    var editor = new GradientEditor(
                        gradient: grad,
                        onChanged: g =>
                        {
                            var node2 = _state.Graph.FindNode(nodeId);
                            var old = node2 is not null &&
                                      node2.Properties.TryGetValue(key: propKey, value: out var o)
                                ? o
                                : propDef.DefaultValue;
                            // Suppress the rebuild this triggers so the stop being dragged is not torn down mid-drag.
                            _suppressRebuild = true;
                            try
                            {
                                _state.Commands.Execute(
                                    new ChangeNodePropertyCommand(
                                        nodeId: nodeId,
                                        propertyKey: propKey,
                                        oldValue: old,
                                        newValue: GraphValue.FromString(SerializeGradient(g))
                                    )
                                );
                            }
                            finally
                            {
                                _suppressRebuild = false;
                            }
                        }
                    );
                    _content.Children.Add(new SizedBox(height: 46f, child: editor));
                }
                else if (prop.Type == GraphTypeRef.Int &&
                         propDef.EnumLabels is { Length: > 0 } labels)
                {
                    int idx = value.IsNull
                        ? 0
                        : Math.Clamp(value: value.AsInt(), min: 0, max: labels.Length - 1);
                    var dd = new Dropdown<string>(
                        items: labels,
                        selectedIndex: idx,
                        displayText: s => s,
                        onChanged: (i, _) =>
                        {
                            var old = node.Properties.TryGetValue(key: propKey, value: out var o)
                                ? o
                                : propDef.DefaultValue;
                            _state.Commands.Execute(
                                new ChangeNodePropertyCommand(
                                    nodeId: nodeId,
                                    propertyKey: propKey,
                                    oldValue: old,
                                    newValue: GraphValue.FromInt(i)
                                )
                            );
                        }
                    ) { Height = 26f };
                    _content.Children.Add(new SizedBox(height: 26f, child: dd));
                }
                else if (prop.Type == GraphTypeRef.Float)
                {
                    float floatVal = value.IsNull ? 0f : value.AsFloat();
                    var field = new NumberInput(floatVal) {
                        Step = 0.01f,
                        Decimals = 3,
                        Min = propDef.Min ?? float.NegativeInfinity,
                        Max = propDef.Max ?? float.PositiveInfinity,
                    };
                    field.OnChanged = v =>
                    {
                        var old = node.Properties.TryGetValue(key: propKey, value: out var o)
                            ? o
                            : propDef.DefaultValue;
                        _state.Commands.Execute(
                            new ChangeNodePropertyCommand(
                                nodeId: nodeId,
                                propertyKey: propKey,
                                oldValue: old,
                                newValue: GraphValue.FromFloat(v)
                            )
                        );
                    };
                    _content.Children.Add(new SizedBox(height: 26f, child: field));
                }
                else if (prop.Type == GraphTypeRef.Bool)
                {
                    bool boolVal = !value.IsNull && value.AsBool();
                    var cb = new Checkbox(
                        value: boolVal,
                        onChanged: v =>
                        {
                            var old = node.Properties.TryGetValue(key: propKey, value: out var o)
                                ? o
                                : propDef.DefaultValue;
                            _state.Commands.Execute(
                                new ChangeNodePropertyCommand(
                                    nodeId: nodeId,
                                    propertyKey: propKey,
                                    oldValue: old,
                                    newValue: GraphValue.FromBool(v)
                                )
                            );
                        }
                    );
                    _content.Children.Add(new SizedBox(height: 26f, child: cb));
                }
                else if (prop.Type == GraphTypeRef.String)
                {
                    string strVal = value.IsNull ? "" : value.AsString();
                    var tf = new TextField(decoration: new InputDecoration(prop.DisplayName)) {
                        Text = strVal,
                    };
                    tf.OnChanged = v =>
                    {
                        var old = node.Properties.TryGetValue(key: propKey, value: out var o)
                            ? o
                            : propDef.DefaultValue;
                        _state.Commands.Execute(
                            new ChangeNodePropertyCommand(
                                nodeId: nodeId,
                                propertyKey: propKey,
                                oldValue: old,
                                newValue: GraphValue.FromString(v)
                            )
                        );
                    };
                    _content.Children.Add(new SizedBox(height: 28f, child: tf));
                }
                else if (prop.Type == GraphTypeRef.Color)
                {
                    float[] rgba = value.IsNull ? [0.8f, 0.8f, 0.8f, 1f] : value.AsFloat4();
                    // Inline editor (swatch + R/G/B sliders). A popover picker is unreliable inside the
                    // modal graph-editor dialog (the scrim can swallow clicks), so edit colour in place.
                    _content.Children.Add(
                        BuildColorEditor(
                            nodeId: node.Id,
                            propDef: propDef,
                            propKey: propKey,
                            rgba: rgba
                        )
                    );
                }
                else
                {
                    _content.Children.Add(
                        new Label(
                            text: $"({prop.Type.Id}): {value}",
                            fontSize: _theme.FontSizeCaption,
                            color: _theme.Hint
                        )
                    );
                }
            }
        }

        // Diagnostics for this node
        var diags = _state.LastValidation?.Diagnostics
            .Where(d => d.NodeId == selectedId)
            .ToList();
        if (diags is { Count: > 0 })
        {
            _content.Children.Add(new SizedBox(height: 8f));
            _content.Children.Add(
                new Label(text: "Diagnostics", fontSize: _theme.FontSizeCaption, color: _theme.Hint)
            );
            _content.Children.Add(new Divider());
            foreach (var d in diags)
            {
                var dc = d.Severity switch {
                    GraphDiagnosticSeverity.Error => _theme.Error,
                    GraphDiagnosticSeverity.Warning => _theme.Accent,
                    _ => _theme.OnSurface,
                };
                _content.Children.Add(
                    new Label(
                        text: $"[{d.Code}] {d.Message}",
                        fontSize: _theme.FontSizeCaption,
                        color: dc
                    )
                );
            }
        }
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
            var slider = new Slider(value) {
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

    // ── Gradient (Color Ramp) property — flat "pos,r,g,b,a,…" string ↔ ColorGradient ─────────────

    private static ColorGradient ParseGradient(string s)
    {
        var stops = new List<GradientStop>();
        if (!string.IsNullOrWhiteSpace(s))
        {
            string[] parts = s.Split(
                separator: ',',
                options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            for (int i = 0; i + 4 < parts.Length; i += 5)
            {
                stops.Add(
                    new GradientStop(
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

        return stops.Count >= 2 ? new ColorGradient(stops) : new ColorGradient();

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

    private static string SerializeGradient(ColorGradient g)
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
        _content.Children.Add(
            new Label(text: "Graph", fontSize: _theme.FontSizeTitle, color: _theme.OnSurface)
        );
        _content.Children.Add(new SizedBox(height: 2f));
        _content.Children.Add(
            new Label(
                text: g.Name.Length > 0 ? g.Name : "(untitled)",
                fontSize: _theme.FontSizeBody,
                color: _theme.Hint
            )
        );
        _content.Children.Add(new SizedBox(height: 8f));
        _content.Children.Add(new Divider());
        _content.Children.Add(new SizedBox(height: 8f));

        _content.Children.Add(
            new Label(
                text: $"Nodes: {g.Nodes.Count}",
                fontSize: _theme.FontSizeBody,
                color: _theme.OnSurface
            )
        );
        _content.Children.Add(new SizedBox(height: 2f));
        _content.Children.Add(
            new Label(
                text: $"Edges: {g.Edges.Count}",
                fontSize: _theme.FontSizeBody,
                color: _theme.OnSurface
            )
        );
        _content.Children.Add(new SizedBox(height: 2f));
        _content.Children.Add(
            new Label(
                text: $"Domain: {g.DomainId}",
                fontSize: _theme.FontSizeCaption,
                color: _theme.Hint
            )
        );

        // Compile output
        var cr = _state.LastCompileResult;
        if (cr is not null)
        {
            _content.Children.Add(new SizedBox(height: 12f));
            _content.Children.Add(
                new Label(
                    text: "Build Output",
                    fontSize: _theme.FontSizeCaption,
                    color: _theme.Hint
                )
            );
            _content.Children.Add(new Divider());
            _content.Children.Add(new SizedBox(height: 4f));

            var statusColor = cr.Success ? _theme.Success : _theme.Error;
            _content.Children.Add(
                new Label(
                    text: cr.Success ? "✓ Build succeeded" : "✗ Build failed",
                    fontSize: _theme.FontSizeBody,
                    color: statusColor
                )
            );

            if (cr.CompiledArtifact is string artifact && artifact.Length > 0)
            {
                _content.Children.Add(new SizedBox(height: 6f));
                // Show artifact lines individually so they wrap properly in the narrow panel
                foreach (string line in artifact.Split(
                             separator: '\n',
                             options: StringSplitOptions.RemoveEmptyEntries
                         ))
                {
                    _content.Children.Add(
                        new Label(
                            text: line.TrimEnd(),
                            fontSize: _theme.FontSizeCaption,
                            color: _theme.OnSurface
                        )
                    );
                }
            }

            // Show diagnostics
            if (cr.Diagnostics.Count > 0)
            {
                _content.Children.Add(new SizedBox(height: 6f));
                foreach (var d in cr.Diagnostics)
                {
                    var dc = d.Severity switch {
                        GraphDiagnosticSeverity.Error => _theme.Error,
                        GraphDiagnosticSeverity.Warning => _theme.Accent,
                        _ => _theme.OnSurface,
                    };
                    _content.Children.Add(
                        new Label(
                            text: $"[{d.Code}] {d.Message}",
                            fontSize: _theme.FontSizeCaption,
                            color: dc
                        )
                    );
                }
            }
        }

        _content.Children.Add(new SizedBox(height: 12f));
        _content.Children.Add(
            new Label(
                text: "Click a node to inspect.",
                fontSize: _theme.FontSizeCaption,
                color: _theme.Hint.WithAlpha(0.5f)
            )
        );
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
