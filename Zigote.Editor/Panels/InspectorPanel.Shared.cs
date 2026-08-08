using System.Globalization;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Editor.History;
using Zigote.Runtime.Scene;
using Zigote.Scripting.Compilation;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
// Dropdown<T> must be referenced with a concrete type — alias for clarity:
using StringDropdown = Zigote.UI.Material.Dropdown<string>;

namespace Zigote.Editor.Panels;

public sealed partial class InspectorPanel
{
    /// <summary>A collapsible section header row; clicking it toggles the section's rows.</summary>
    private PropRow SectionRow(string title, ThemeData theme)
    {
        var collapsed = _collapsedSections.Contains(title);
        var header = new SectionHeader(
            title,
            theme,
            collapsed,
            () =>
            {
                if (!_collapsedSections.Remove(title)) _collapsedSections.Add(title);
                Rebuild();
                RequestLayout();
            }
        );
        return PropRow.Section(header, title);
    }

    // ── Property row ──────────────────────────────────────────────────────────

    private sealed class PropRow : Widget
    {
        /// <summary>
        ///     Command history used by scrub-capable rows (Float) to coalesce a drag into one undo entry.
        ///     Set at the top of <see cref="InspectorPanel.Rebuild" />.
        /// </summary>
        internal static CommandHistory? History;

        private readonly Widget _inner;
        private Size _size;

        private PropRow(Widget inner)
        {
            _inner = inner;
        }

        /// <summary>True when this row is a section header (used by collapse filtering).</summary>
        public bool IsSectionHeader { get; private init; }

        /// <summary>The section title this header toggles (set for section headers only).</summary>
        public string? SectionTitle { get; private init; }

        /// <summary>Spacer between sections.</summary>
        public static PropRow Spacer(float height)
        {
            return new PropRow(new SizedBox(height: height));
        }

        /// <summary>Full-width action button row.</summary>
        public static PropRow ActionButton(string label, Action onClick)
        {
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new SizedBox(height: 26f, child: new Button(label, onClick))
                )
            );
        }

        /// <summary>Single-line status text (build result summary, "Building...", etc.).</summary>
        public static PropRow StatusLine(string text, Color color, ThemeData theme)
        {
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        2f
                    ),
                    new Label(text, theme.FontSizeCaption, color)
                )
            );
        }

        /// <summary>One compiler diagnostic displayed as a small indented row.</summary>
        public static PropRow DiagnosticLine(ScriptDiagnostic d, ThemeData theme)
        {
            var color = d.Severity == DiagnosticSeverity.Error ? theme.Error : theme.Accent;
            var file = d.File != null ? System.IO.Path.GetFileName(d.File) : "";
            var loc = file.Length > 0 ? $"{file}({d.Line}): " : "";
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        8f,
                        0f,
                        0f,
                        1f
                    ),
                    new Label(
                        $"{loc}{d.Message}",
                        theme.FontSizeCaption - 1f,
                        color.WithAlpha(0.85f)
                    )
                )
            );
        }

        /// <summary>Node name field + kind badge at the top of the inspector.</summary>
        public static PropRow NodeHeader(TextField nameField, NodeKind kind, ThemeData theme)
        {
            var kindColor = kind switch {
                NodeKind.Mesh => new Color(0.4f, 0.75f, 1f),
                NodeKind.Light => new Color(1f, 0.88f, 0.3f),
                NodeKind.Camera => new Color(0.35f, 0.9f, 0.5f),
                NodeKind.Script => new Color(0.75f, 0.45f, 1f),
                _ => theme.Hint,
            };
            var kindLabel = kind.ToString().ToUpper();

            return new PropRow(
                new Column {
                    MainAxisAlignment = MainAxisAlignment.Start,
                    CrossAxisAlignment = CrossAxisAlignment.Start,
                    Children = {
                        // Kind badge
                        new Padding(
                            new EdgeInsets(
                                0f,
                                0f,
                                0f,
                                4f
                            ),
                            new Label(kindLabel, theme.FontSizeCaption - 1f, kindColor) {
                                FontWeight = FontWeight.Bold,
                            }
                        ),
                        // Editable name
                        new SizedBox(height: 28f, child: nameField),
                    },
                }
            );
        }

        /// <summary>Wrap a section-header widget, tagged so collapse can hide the section's rows.</summary>
        public static PropRow Section(Widget header, string title)
        {
            return new PropRow(header) {
                IsSectionHeader = true,
                SectionTitle = title,
            };
        }

        public static PropRow Text(string label, string value,
            Action<string> onChange, ThemeData theme, App app)
        {
            var tf = new TextField {
                Text = value,
                OnChanged = onChange,
            };
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child:
                                new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new SizedBox(height: 24f, child: tf),
                        },
                    }
                )
            );
        }

        /// <summary>Free-text field with a type-ahead suggestion popup (commits on pick/Enter).</summary>
        public static PropRow Suggest(string label, string value,
            Func<string, IReadOnlyList<(string Value, string Display)>> suggest,
            Action<string> onCommit, ThemeData theme, App app)
        {
            var f = new AutoSuggestField(
                app,
                value,
                suggest,
                onCommit
            ) { Height = 24f };
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new Expanded(new SizedBox(height: 24f, child: f)),
                        },
                    }
                )
            );
        }

        /// <summary>Label + a clickable colour swatch that opens a preset/RGB picker.</summary>
        public static PropRow ColorSwatch(string label, Vec3 current, Action<Vec3> setter,
            ThemeData theme, App app)
        {
            var sw = new ColorSwatchField(new Color(current.X, current.Y, current.Z), app) {
                OnChanged = c => setter(new Vec3(c.R, c.G, c.B)),
            };
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            sw,
                        },
                    }
                )
            );
        }

        /// <summary>Wrap an arbitrary widget as a property row.</summary>
        public static PropRow Custom(Widget inner)
        {
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    inner
                )
            );
        }

        public static PropRow Path(string label, string value,
            Action<string> onChange, string rootPath, string[] extensions, ThemeData theme, App app)
        {
            var tf = new TextField {
                Text = value,
                OnChanged = onChange,
            };
            var pickBtn = new SizedBox(
                24f,
                24f,
                new Button(
                    "...",
                    () =>
                    {
                        FilePickerDialog.Show(
                            app,
                            "Select " + label,
                            rootPath,
                            extensions,
                            selectedPath =>
                            {
                                tf.Text = selectedPath;
                                onChange(selectedPath);
                            }
                        );
                    }
                ) {
                    Padding = EdgeInsets.Zero,
                }
            );
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child:
                                new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new Expanded(new SizedBox(height: 24f, child: tf)),
                            new SizedBox(4f),
                            pickBtn,
                        },
                    }
                )
            );
        }

        public static PropRow DropdownRow(string label, string[] items, int selectedIndex,
            Action<int> onChange, ThemeData theme)
        {
            var dd = new StringDropdown(
                items,
                selectedIndex,
                s => s,
                (i, _) => onChange(i)
            );
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new SizedBox(height: 24f, width: 130f, child: dd),
                        },
                    }
                )
            );
        }

        public static PropRow Toggle(string label, bool value, Action<bool> onChange,
            ThemeData theme)
        {
            var cb = new Checkbox(value, onChange);
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            cb,
                        },
                    }
                )
            );
        }

        public static PropRow Float(string label, float value, Action<float> onChange,
            ThemeData theme,
            float min = 0f, float max = 1f, float step = 0.05f)
        {
            var ni = new NumberInput(
                value,
                step,
                min,
                max
            ) { Decimals = 2 };
            ni.OnChanged = onChange;
            ni.OnScrubStart = () => History?.BeginInteraction();
            ni.OnScrubEnd = () => History?.EndInteraction();
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new SizedBox(height: 26f, width: 110f, child: ni),
                        },
                    }
                )
            );
        }

        // ── NodeBind<T> overloads — route mutations through the command history ──

        public static PropRow Float(string label, NodeBind<float> bind, ThemeData theme,
            float min = 0f, float max = 1f, float step = 0.05f)
        {
            var ni = new NumberInput(
                bind.Value,
                step,
                min,
                max
            ) { Decimals = 2 };
            ni.OnChanged = bind.Set;
            ni.OnScrubStart = bind.BeginEdit;
            ni.OnScrubEnd = bind.EndEdit;
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new SizedBox(height: 26f, width: 110f, child: ni),
                        },
                    }
                )
            );
        }

        public static PropRow Toggle(string label, NodeBind<bool> bind, ThemeData theme)
        {
            var cb = new Checkbox(bind.Value, bind.Set);
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            cb,
                        },
                    }
                )
            );
        }

        public static PropRow ColorSwatch(string label, NodeBind<Vec3> bind, ThemeData theme,
            App app)
        {
            var v = bind.Value;
            var sw = new ColorSwatchField(new Color(v.X, v.Y, v.Z), app) {
                OnChanged = c => bind.Set(new Vec3(c.R, c.G, c.B)),
            };
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            sw,
                        },
                    }
                )
            );
        }

        public static PropRow Vec3(string label, NodeBind<Vec3> bind, ThemeData theme)
        {
            var current = bind.Value;
            var tfX = MiniFloat(current.X.ToString("F2"), theme);
            var tfY = MiniFloat(current.Y.ToString("F2"), theme);
            var tfZ = MiniFloat(current.Z.ToString("F2"), theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : 0f;
            }

            tfX.OnChanged = s => bind.Set(new Vec3(Parse(s), Parse(tfY.Text), Parse(tfZ.Text)));
            tfY.OnChanged = s => bind.Set(new Vec3(Parse(tfX.Text), Parse(s), Parse(tfZ.Text)));
            tfZ.OnChanged = s => bind.Set(new Vec3(Parse(tfX.Text), Parse(tfY.Text), Parse(s)));

            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        6f
                    ),
                    new Column {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Start,
                        Children = {
                            new Label(label, theme.FontSizeCaption, theme.Hint),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlignment = MainAxisAlignment.Start,
                                CrossAxisAlignment = CrossAxisAlignment.Center,
                                Children = {
                                    new Label("X", theme.FontSizeCaption, theme.Accent),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfX),
                                    new SizedBox(8f),
                                    new Label("Y", theme.FontSizeCaption, theme.Success),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfY),
                                    new SizedBox(8f),
                                    new Label("Z", theme.FontSizeCaption, theme.Primary),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfZ),
                                },
                            },
                        },
                    }
                )
            );
        }

        public static PropRow Vec3Color(string label, NodeBind<Vec3> bind, ThemeData theme)
        {
            var current = bind.Value;
            var tfR = MiniFloat(current.X.ToString("F2"), theme);
            var tfG = MiniFloat(current.Y.ToString("F2"), theme);
            var tfB = MiniFloat(current.Z.ToString("F2"), theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : 0f;
            }

            tfR.OnChanged = s => bind.Set(new Vec3(Parse(s), Parse(tfG.Text), Parse(tfB.Text)));
            tfG.OnChanged = s => bind.Set(new Vec3(Parse(tfR.Text), Parse(s), Parse(tfB.Text)));
            tfB.OnChanged = s => bind.Set(new Vec3(Parse(tfR.Text), Parse(tfG.Text), Parse(s)));

            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        6f
                    ),
                    new Column {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Start,
                        Children = {
                            new Label(label, theme.FontSizeCaption, theme.Hint),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlignment = MainAxisAlignment.Start,
                                CrossAxisAlignment = CrossAxisAlignment.Center,
                                Children = {
                                    new Label(
                                        "R",
                                        theme.FontSizeCaption,
                                        new Color(0.9f, 0.35f, 0.35f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfR),
                                    new SizedBox(6f),
                                    new Label(
                                        "G",
                                        theme.FontSizeCaption,
                                        new Color(0.3f, 0.85f, 0.3f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfG),
                                    new SizedBox(6f),
                                    new Label(
                                        "B",
                                        theme.FontSizeCaption,
                                        new Color(0.3f, 0.55f, 1.0f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfB),
                                },
                            },
                        },
                    }
                )
            );
        }

        public static PropRow Vec3Color(string label, Vec3 current, Action<Vec3> setter,
            ThemeData theme)
        {
            var tfR = MiniFloat(current.X.ToString("F2"), theme);
            var tfG = MiniFloat(current.Y.ToString("F2"), theme);
            var tfB = MiniFloat(current.Z.ToString("F2"), theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : 0f;
            }

            tfR.OnChanged = s => setter(new Vec3(Parse(s), Parse(tfG.Text), Parse(tfB.Text)));
            tfG.OnChanged = s => setter(new Vec3(Parse(tfR.Text), Parse(s), Parse(tfB.Text)));
            tfB.OnChanged = s => setter(new Vec3(Parse(tfR.Text), Parse(tfG.Text), Parse(s)));

            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        6f
                    ),
                    new Column {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Start,
                        Children = {
                            new Label(label, theme.FontSizeCaption, theme.Hint),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlignment = MainAxisAlignment.Start,
                                CrossAxisAlignment = CrossAxisAlignment.Center,
                                Children = {
                                    new Label(
                                        "R",
                                        theme.FontSizeCaption,
                                        new Color(0.9f, 0.35f, 0.35f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfR),
                                    new SizedBox(6f),
                                    new Label(
                                        "G",
                                        theme.FontSizeCaption,
                                        new Color(0.3f, 0.85f, 0.3f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfG),
                                    new SizedBox(6f),
                                    new Label(
                                        "B",
                                        theme.FontSizeCaption,
                                        new Color(0.3f, 0.55f, 1.0f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfB),
                                },
                            },
                        },
                    }
                )
            );
        }

        public static PropRow Vec3(string label, Vec3 current, Action<Vec3> setter, ThemeData theme)
        {
            var tfX = MiniFloat(current.X.ToString("F2"), theme);
            var tfY = MiniFloat(current.Y.ToString("F2"), theme);
            var tfZ = MiniFloat(current.Z.ToString("F2"), theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : 0f;
            }

            tfX.OnChanged = s => setter(new Vec3(Parse(s), Parse(tfY.Text), Parse(tfZ.Text)));
            tfY.OnChanged = s => setter(new Vec3(Parse(tfX.Text), Parse(s), Parse(tfZ.Text)));
            tfZ.OnChanged = s => setter(new Vec3(Parse(tfX.Text), Parse(tfY.Text), Parse(s)));

            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        6f
                    ),
                    new Column {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Start,
                        Children = {
                            new Label(label, theme.FontSizeCaption, theme.Hint),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlignment = MainAxisAlignment.Start,
                                CrossAxisAlignment = CrossAxisAlignment.Center,
                                Children = {
                                    new Label("X", theme.FontSizeCaption, theme.Accent),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfX),
                                    new SizedBox(8f),
                                    new Label("Y", theme.FontSizeCaption, theme.Success),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfY),
                                    new SizedBox(8f),
                                    new Label("Z", theme.FontSizeCaption, theme.Primary),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfZ),
                                },
                            },
                        },
                    }
                )
            );
        }

        private static TextField MiniFloat(string val, ThemeData theme)
        {
            return new TextField {
                Text = val,
                MinWidth = 64f,
                Height = 22f,
            };
        }

        public override Size Measure(Constraints c)
        {
            _size = _inner.Measure(c);
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
            _inner.Layout(origin);
        }

        public override void Paint(PaintList paint)
        {
            _inner.Paint(paint);
        }

        public override Widget? HitTest(Offset point)
        {
            if (!Bounds.Contains(point.X, point.Y)) return null;
            return _inner.HitTest(point);
        }
    }

    // ── Section header widget ─────────────────────────────────────────────────

    /// <summary>
    ///     A collapsible section header: a disclosure chevron, a title and a full-width hairline.
    ///     Clicking anywhere toggles the section (the panel hides the rows beneath a collapsed header).
    /// </summary>
    private sealed class SectionHeader : Widget
    {
        private readonly bool _collapsed;
        private readonly Action _onToggle;
        private readonly ThemeData _theme;
        private readonly string _title;
        private bool _hovered;
        private Size _size;

        public SectionHeader(string title, ThemeData theme, bool collapsed, Action onToggle)
        {
            _title = title;
            _theme = theme;
            _collapsed = collapsed;
            _onToggle = onToggle;
        }

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(c.MaxWidth, 26f));
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
        }

        public override void Paint(PaintList paint)
        {
            if (_hovered)
                paint.AddRect(
                    new Rect(
                        Bounds.X,
                        Bounds.Y,
                        Bounds.Width,
                        Bounds.Height - 1f
                    ),
                    _theme.ControlHover,
                    4f
                );

            // Disclosure chevron — ▾ expanded, ▸ collapsed.
            const float cs = 14f;
            var chevron = _collapsed ? Icons.ChevronRight : Icons.ChevronDown;
            Icons.Draw(
                paint,
                chevron,
                new Rect(
                    Bounds.X,
                    Bounds.Y,
                    cs,
                    Bounds.Height
                ),
                _theme.TextSecondary,
                cs
            );

            var fs = _theme.FontSizeCaption;
            var ty = Bounds.Y + (Bounds.Height - fs) / 2f + fs * 0.8f;
            paint.AddText(
                _title,
                Bounds.X + cs + 2f,
                ty,
                _theme.OnSurface,
                fs,
                fontWeight: FontWeight.SemiBold
            );

            // Full-width hairline closing the header band off from the rows below.
            paint.AddRect(
                new Rect(
                    Bounds.X,
                    Bounds.Bottom - 1f,
                    Bounds.Width,
                    1f
                ),
                _theme.Separator
            );
        }

        public override void OnPointerEnter()
        {
            _hovered = true;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            _hovered = false;
            MarkNeedsPaint();
        }

        public override void OnPointerUp(Offset point)
        {
            if (Bounds.Contains(point.X, point.Y)) _onToggle();
        }
    }
}