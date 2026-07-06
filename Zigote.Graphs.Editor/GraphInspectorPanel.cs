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
            MainAxisAlign = MainAxisAlignment.Start,
            CrossAxisAlign = CrossAxisAlignment.Stretch,
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
                def?.DisplayName ?? node.DefinitionId,
                _theme.FontSizeTitle,
                _theme.OnSurface
            )
        );
        _content.Children.Add(new SizedBox(height: 4f));

        if (def is null)
        {
            _content.Children.Add(
                new Label(
                    $"Definition '{node.DefinitionId}' not found",
                    _theme.FontSizeBody,
                    _theme.Error
                )
            );
            return;
        }

        // Category + description
        if (!string.IsNullOrWhiteSpace(def.Category))
            _content.Children.Add(new Label(def.Category, _theme.FontSizeCaption, _theme.Hint));

        if (!string.IsNullOrWhiteSpace(def.Description))
        {
            _content.Children.Add(new SizedBox(height: 4f));
            _content.Children.Add(
                new Label(def.Description, _theme.FontSizeCaption, _theme.OnSurface.WithAlpha(0.7f))
            );
        }

        // Properties
        if (def.Properties.Count > 0)
        {
            _content.Children.Add(new SizedBox(height: 8f));
            _content.Children.Add(new Label("Properties", _theme.FontSizeCaption, _theme.Hint));
            _content.Children.Add(new Divider());

            foreach (var prop in def.Properties)
            {
                var propDef = prop;
                var nodeId = node.Id;
                var propKey = prop.Id;
                node.Properties.TryGetValue(propKey, out var current);
                var value = current ?? prop.DefaultValue;

                _content.Children.Add(new SizedBox(height: 4f));
                _content.Children.Add(
                    new Label(prop.DisplayName, _theme.FontSizeBody, _theme.OnSurface)
                );

                if (propDef.Editor == "gradient" && prop.Type == GraphTypeRef.String)
                {
                    var grad = ParseGradient(value.IsNull ? "" : value.AsString());
                    var editor = new GradientEditor(
                        grad,
                        g =>
                        {
                            var node2 = _state.Graph.FindNode(nodeId);
                            var old = node2 is not null &&
                                      node2.Properties.TryGetValue(propKey, out var o)
                                ? o
                                : propDef.DefaultValue;
                            // Suppress the rebuild this triggers so the stop being dragged is not torn down mid-drag.
                            _suppressRebuild = true;
                            try
                            {
                                _state.Commands.Execute(
                                    new ChangeNodePropertyCommand(
                                        nodeId,
                                        propKey,
                                        old,
                                        GraphValue.FromString(SerializeGradient(g))
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
                    var idx = value.IsNull ? 0 : Math.Clamp(value.AsInt(), 0, labels.Length - 1);
                    var dd = new Dropdown<string>(
                        labels,
                        idx,
                        s => s,
                        (i, _) =>
                        {
                            var old = node.Properties.TryGetValue(propKey, out var o)
                                ? o
                                : propDef.DefaultValue;
                            _state.Commands.Execute(
                                new ChangeNodePropertyCommand(
                                    nodeId,
                                    propKey,
                                    old,
                                    GraphValue.FromInt(i)
                                )
                            );
                        }
                    ) { Height = 26f };
                    _content.Children.Add(new SizedBox(height: 26f, child: dd));
                }
                else if (prop.Type == GraphTypeRef.Float)
                {
                    var floatVal = value.IsNull ? 0f : value.AsFloat();
                    var field = new NumberInput(floatVal) {
                        Step = 0.01f,
                        Decimals = 3,
                        Min = propDef.Min ?? float.NegativeInfinity,
                        Max = propDef.Max ?? float.PositiveInfinity,
                    };
                    field.OnChanged = v =>
                    {
                        var old = node.Properties.TryGetValue(propKey, out var o)
                            ? o
                            : propDef.DefaultValue;
                        _state.Commands.Execute(
                            new ChangeNodePropertyCommand(
                                nodeId,
                                propKey,
                                old,
                                GraphValue.FromFloat(v)
                            )
                        );
                    };
                    _content.Children.Add(new SizedBox(height: 26f, child: field));
                }
                else if (prop.Type == GraphTypeRef.Bool)
                {
                    var boolVal = !value.IsNull && value.AsBool();
                    var cb = new Checkbox(
                        boolVal,
                        v =>
                        {
                            var old = node.Properties.TryGetValue(propKey, out var o)
                                ? o
                                : propDef.DefaultValue;
                            _state.Commands.Execute(
                                new ChangeNodePropertyCommand(
                                    nodeId,
                                    propKey,
                                    old,
                                    GraphValue.FromBool(v)
                                )
                            );
                        }
                    );
                    _content.Children.Add(new SizedBox(height: 26f, child: cb));
                }
                else if (prop.Type == GraphTypeRef.String)
                {
                    var strVal = value.IsNull ? "" : value.AsString();
                    var tf = new TextField(decoration: new InputDecoration(prop.DisplayName)) {
                        Text = strVal,
                    };
                    tf.OnChanged = v =>
                    {
                        var old = node.Properties.TryGetValue(propKey, out var o)
                            ? o
                            : propDef.DefaultValue;
                        _state.Commands.Execute(
                            new ChangeNodePropertyCommand(
                                nodeId,
                                propKey,
                                old,
                                GraphValue.FromString(v)
                            )
                        );
                    };
                    _content.Children.Add(new SizedBox(height: 28f, child: tf));
                }
                else if (prop.Type == GraphTypeRef.Color)
                {
                    var rgba = value.IsNull ? [0.8f, 0.8f, 0.8f, 1f] : value.AsFloat4();
                    // Inline editor (swatch + R/G/B sliders). A popover picker is unreliable inside the
                    // modal graph-editor dialog (the scrim can swallow clicks), so edit colour in place.
                    _content.Children.Add(
                        BuildColorEditor(
                            node.Id,
                            propDef,
                            propKey,
                            rgba
                        )
                    );
                }
                else
                {
                    _content.Children.Add(
                        new Label(
                            $"({prop.Type.Id}): {value}",
                            _theme.FontSizeCaption,
                            _theme.Hint
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
            _content.Children.Add(new Label("Diagnostics", _theme.FontSizeCaption, _theme.Hint));
            _content.Children.Add(new Divider());
            foreach (var d in diags)
            {
                var dc = d.Severity switch {
                    GraphDiagnosticSeverity.Error => _theme.Error,
                    GraphDiagnosticSeverity.Warning => _theme.Accent,
                    _ => _theme.OnSurface,
                };
                _content.Children.Add(
                    new Label($"[{d.Code}] {d.Message}", _theme.FontSizeCaption, dc)
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
        var r = rgba[0];
        var g = rgba[1];
        var b = rgba[2];
        var a = rgba.Length > 3 ? rgba[3] : 1f;

        var swatch = new ColorSwatch(new Color(r, g, b), _theme);

        void Commit()
        {
            swatch.Value = new Color(r, g, b);
            swatch.MarkNeedsPaint();
            var node = _state.Graph.FindNode(nodeId);
            var old = node is not null && node.Properties.TryGetValue(propKey, out var o)
                ? o
                : propDef.DefaultValue;
            // Suppress the rebuild this triggers so the slider being dragged is not torn down mid-drag.
            _suppressRebuild = true;
            try
            {
                _state.Commands.Execute(
                    new ChangeNodePropertyCommand(
                        nodeId,
                        propKey,
                        old,
                        GraphValue.FromFloat4(
                            r,
                            g,
                            b,
                            a
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
                    MainAxisAlign = MainAxisAlignment.Start,
                    CrossAxisAlign = CrossAxisAlignment.Center,
                    Children = {
                        new SizedBox(
                            14f,
                            child: new Label(label, _theme.FontSizeCaption, _theme.Hint)
                        ),
                        new SizedBox(6f),
                        new Expanded(slider),
                    },
                }
            );
        }

        return new Column {
            MainAxisAlign = MainAxisAlignment.Start,
            CrossAxisAlign = CrossAxisAlignment.Stretch,
            Children = {
                new SizedBox(height: 22f, child: swatch),
                new SizedBox(height: 4f),
                Channel("R", r, v => r = v),
                Channel("G", g, v => g = v),
                Channel("B", b, v => b = v),
            },
        };
    }

    // ── Gradient (Color Ramp) property — flat "pos,r,g,b,a,…" string ↔ ColorGradient ─────────────

    private static ColorGradient ParseGradient(string s)
    {
        var stops = new List<GradientStop>();
        if (!string.IsNullOrWhiteSpace(s))
        {
            var parts = s.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            for (var i = 0; i + 4 < parts.Length; i += 5)
                stops.Add(
                    new GradientStop(
                        P(parts[i]),
                        new Color(
                            P(parts[i + 1]),
                            P(parts[i + 2]),
                            P(parts[i + 3]),
                            P(parts[i + 4])
                        )
                    )
                );
        }

        return stops.Count >= 2 ? new ColorGradient(stops) : new ColorGradient();

        static float P(string x)
        {
            return float.TryParse(
                x,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var v
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

        return string.Join(',', fields);

        static string C(float v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    private void RebuildGraphSummary()
    {
        var g = _state.Graph;
        _content.Children.Add(new Label("Graph", _theme.FontSizeTitle, _theme.OnSurface));
        _content.Children.Add(new SizedBox(height: 2f));
        _content.Children.Add(
            new Label(
                g.Name.Length > 0 ? g.Name : "(untitled)",
                _theme.FontSizeBody,
                _theme.Hint
            )
        );
        _content.Children.Add(new SizedBox(height: 8f));
        _content.Children.Add(new Divider());
        _content.Children.Add(new SizedBox(height: 8f));

        _content.Children.Add(
            new Label($"Nodes: {g.Nodes.Count}", _theme.FontSizeBody, _theme.OnSurface)
        );
        _content.Children.Add(new SizedBox(height: 2f));
        _content.Children.Add(
            new Label($"Edges: {g.Edges.Count}", _theme.FontSizeBody, _theme.OnSurface)
        );
        _content.Children.Add(new SizedBox(height: 2f));
        _content.Children.Add(
            new Label($"Domain: {g.DomainId}", _theme.FontSizeCaption, _theme.Hint)
        );

        // Compile output
        var cr = _state.LastCompileResult;
        if (cr is not null)
        {
            _content.Children.Add(new SizedBox(height: 12f));
            _content.Children.Add(new Label("Build Output", _theme.FontSizeCaption, _theme.Hint));
            _content.Children.Add(new Divider());
            _content.Children.Add(new SizedBox(height: 4f));

            var statusColor = cr.Success ? _theme.Success : _theme.Error;
            _content.Children.Add(
                new Label(
                    cr.Success ? "✓ Build succeeded" : "✗ Build failed",
                    _theme.FontSizeBody,
                    statusColor
                )
            );

            if (cr.CompiledArtifact is string artifact && artifact.Length > 0)
            {
                _content.Children.Add(new SizedBox(height: 6f));
                // Show artifact lines individually so they wrap properly in the narrow panel
                foreach (var line in artifact.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    _content.Children.Add(
                        new Label(line.TrimEnd(), _theme.FontSizeCaption, _theme.OnSurface)
                    );
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
                        new Label($"[{d.Code}] {d.Message}", _theme.FontSizeCaption, dc)
                    );
                }
            }
        }

        _content.Children.Add(new SizedBox(height: 12f));
        _content.Children.Add(
            new Label(
                "Click a node to inspect.",
                _theme.FontSizeCaption,
                _theme.Hint.WithAlpha(0.5f)
            )
        );
    }

    public override Size Measure(Constraints c)
    {
        // Width follows the constraint; height sizes to content. This panel lives inside a vertical
        // ScrollView, where c.MaxHeight is ∞ — filling it would report an infinite scroll extent
        // (a drag then drives the offset to ∞, and the scrollbar computes ∞/∞ = NaN → paint crash).
        // A tight-width / loose-height measure lets the Column report its natural stacked height.
        var width = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
        var content = _content.Measure(
            new Constraints(
                width,
                width,
                0f,
                c.MaxHeight
            )
        );
        _size = c.Constrain(new Size(width, content.Height));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _content.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _content.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? _content.HitTest(point) ?? this : null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_content];
    }

    /// <summary>A non-interactive colour preview bar for the inline colour editor.</summary>
    private sealed class ColorSwatch(Color value, ThemeData theme) : Widget
    {
        private Size _s;
        public Color Value { get; set; } = value;

        public override Size Measure(Constraints c)
        {
            _s = c.Constrain(new Size(c.MaxWidth, 22f));
            return _s;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                _s.Width,
                _s.Height
            );
        }

        public override void Paint(PaintList paint)
        {
            paint.AddRect(Bounds, Value.WithAlpha(1f), Radii.Sm);
            paint.AddBorder(Bounds, theme.Separator, Radii.Sm);
        }
    }
}