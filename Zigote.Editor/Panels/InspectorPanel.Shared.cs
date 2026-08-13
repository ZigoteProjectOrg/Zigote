using System.Globalization;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Editor.History;
using Zigote.Runtime.Scene;
using Zigote.Scripting.Compilation;
using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
// FilePickerDialog: the in-app file browser. GTK's file chooser is a shell portal with no
// libadwaita widget behind it, so this stays Material until one exists here.

namespace Zigote.Editor.Panels;

public sealed partial class InspectorPanel
{
    /// <summary>A collapsible section header row; clicking it toggles the section's rows.</summary>
    private PropRow SectionRow(string title, ThemeData theme)
    {
        bool collapsed = _collapsedSections.Contains(title);
        var header = new SectionHeader(
            title: title,
            theme: theme,
            collapsed: collapsed,
            onToggle: () =>
            {
                if (!_collapsedSections.Remove(title)) _collapsedSections.Add(title);
                Rebuild();
                RequestLayout();
            }
        );
        return PropRow.Section(header: header, title: title);
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

        private PropRow(Widget inner) => _inner = inner;

        /// <summary>True when this row is a section header (used by collapse filtering).</summary>
        public bool IsSectionHeader { get; private init; }

        /// <summary>The section title this header toggles (set for section headers only).</summary>
        public string? SectionTitle { get; private init; }

        /// <summary>Spacer between sections.</summary>
        public static PropRow Spacer(float height) => new(new SizedBox(height: height));

        /// <summary>Full-width action button row.</summary>
        public static PropRow ActionButton(string label, Action onClick)
        {
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new AdwButton(label: label, onPressed: onClick) { Compact = true }
                )
            );
        }

        /// <summary>Single-line status text (build result summary, "Building...", etc.).</summary>
        public static PropRow StatusLine(string text, Color color, ThemeData theme)
        {
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 2f
                    ),
                    child: new Label(text: text, fontSize: theme.FontSizeCaption, color: color)
                )
            );
        }

        /// <summary>One compiler diagnostic displayed as a small indented row.</summary>
        public static PropRow DiagnosticLine(ScriptDiagnostic d, ThemeData theme)
        {
            var color = d.Severity == DiagnosticSeverity.Error ? theme.Error : theme.Accent;
            string file = d.File != null ? System.IO.Path.GetFileName(d.File) : "";
            string loc = file.Length > 0 ? $"{file}({d.Line}): " : "";
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 8f,
                        top: 0f,
                        right: 0f,
                        bottom: 1f
                    ),
                    child: new Label(
                        text: $"{loc}{d.Message}",
                        fontSize: theme.FontSizeCaption - 1f,
                        color: color.WithAlpha(0.85f)
                    )
                )
            );
        }

        /// <summary>Node name field + kind badge at the top of the inspector.</summary>
        public static PropRow NodeHeader(AdwEntry nameField, NodeKind kind, ThemeData theme)
        {
            var kindColor = kind switch {
                NodeKind.Mesh => new Color(r: 0.4f, g: 0.75f, b: 1f),
                NodeKind.Light => new Color(r: 1f, g: 0.88f, b: 0.3f),
                NodeKind.Camera => new Color(r: 0.35f, g: 0.9f, b: 0.5f),
                NodeKind.Script => new Color(r: 0.75f, g: 0.45f, b: 1f),
                _ => theme.Hint,
            };
            string kindLabel = kind.ToString().ToUpper();

            return new PropRow(
                new Column {
                    MainAxisAlignment = MainAxisAlignment.Start,
                    CrossAxisAlignment = CrossAxisAlignment.Start,
                    Children = {
                        // Kind badge
                        new Padding(
                            padding: new EdgeInsets(
                                left: 0f,
                                top: 0f,
                                right: 0f,
                                bottom: 4f
                            ),
                            child: new Label(
                                text: kindLabel,
                                fontSize: theme.FontSizeCaption - 1f,
                                color: kindColor
                            ) {
                                FontWeight = FontWeight.Bold,
                            }
                        ),
                        // Editable name
                        nameField,
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
            var tf = new AdwEntry {
                Text = value,
                OnChanged = onChange,
                Compact = true,
            };
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child:
                                new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
                            ),
                            new SizedBox(4f),
                            new Expanded(tf),
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
            var f = new AdwSuggestionEntry(value: value, suggest: suggest, onCommit: onCommit) {
                Compact = true,
            };
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child: new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
                            ),
                            new SizedBox(4f),
                            new Expanded(f),
                        },
                    }
                )
            );
        }

        /// <summary>Label + a clickable colour swatch that opens a preset/RGB picker.</summary>
        public static PropRow ColorSwatch(string label, Vec3 current, Action<Vec3> setter,
            ThemeData theme, App app)
        {
            var sw = new AdwColorButton(
                value: new Color(r: current.X, g: current.Y, b: current.Z),
                app: app
            ) {
                OnChanged = c => setter(new Vec3(x: c.R, y: c.G, z: c.B)),
            };
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child: new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
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
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: inner
                )
            );
        }

        public static PropRow Path(string label, string value,
            Action<string> onChange, string rootPath, string[] extensions, ThemeData theme, App app)
        {
            var tf = new AdwEntry {
                Text = value,
                OnChanged = onChange,
                Compact = true,
            };
            var pickBtn = new AdwButton(
                label: "Browse",
                onPressed: () =>
                {
                    FilePickerDialog.Show(
                        app: app,
                        title: "Select " + label,
                        rootPath: rootPath,
                        extensions: extensions,
                        onSelected: selectedPath =>
                        {
                            tf.Text = selectedPath;
                            onChange(selectedPath);
                        }
                    );
                }
            ) {
                IconName = Icons.Folder,
                Style = AdwButtonStyle.Flat,
                Circular = true,
            };
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child:
                                new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
                            ),
                            new SizedBox(4f),
                            new Expanded(tf),
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
            var dd = new AdwDropDown(
                items: items,
                selectedIndex: selectedIndex,
                onSelected: onChange
            ) { Compact = true };
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child: new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
                            ),
                            new SizedBox(4f),
                            new SizedBox(width: 150f, child: dd),
                        },
                    }
                )
            );
        }

        public static PropRow Toggle(string label, bool value, Action<bool> onChange,
            ThemeData theme)
        {
            var cb = new AdwSwitch(value: value, onChanged: onChange);
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child: new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
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
            // Adwaita has no scrub grip, so an edit is a discrete commit: open and close the
            // history interaction around each one rather than around a drag.
            var ni = new AdwSpinButton(
                value: value,
                min: min,
                max: max,
                step: step,
                onChanged: v =>
                {
                    History?.BeginInteraction();
                    onChange((float)v);
                    History?.EndInteraction();
                }
            ) { Compact = true };
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child: new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
                            ),
                            new SizedBox(4f),
                            new SizedBox(width: 120f, child: ni),
                        },
                    }
                )
            );
        }

        // ── NodeBind<T> overloads — route mutations through the command history ──

        public static PropRow Float(string label, NodeBind<float> bind, ThemeData theme,
            float min = 0f, float max = 1f, float step = 0.05f)
        {
            var ni = new AdwSpinButton(
                value: bind.Value,
                min: min,
                max: max,
                step: step,
                onChanged: v =>
                {
                    bind.BeginEdit();
                    bind.Set((float)v);
                    bind.EndEdit();
                }
            ) { Compact = true };
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child: new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
                            ),
                            new SizedBox(4f),
                            new SizedBox(width: 120f, child: ni),
                        },
                    }
                )
            );
        }

        public static PropRow Toggle(string label, NodeBind<bool> bind, ThemeData theme)
        {
            var cb = new AdwSwitch(value: bind.Value, onChanged: bind.Set);
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child: new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
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
            var sw = new AdwColorButton(value: new Color(r: v.X, g: v.Y, b: v.Z), app: app) {
                OnChanged = c => bind.Set(new Vec3(x: c.R, y: c.G, z: c.B)),
            };
            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 4f
                    ),
                    child: new Row {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                width: 76f,
                                child: new Label(
                                    text: label,
                                    fontSize: theme.FontSizeCaption,
                                    color: theme.Hint
                                )
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
            var tfX = MiniFloat(val: current.X.ToString("F2"), theme: theme);
            var tfY = MiniFloat(val: current.Y.ToString("F2"), theme: theme);
            var tfZ = MiniFloat(val: current.Z.ToString("F2"), theme: theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s: s,
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out float v
                )
                    ? v
                    : 0f;
            }

            tfX.OnChanged = s =>
                bind.Set(new Vec3(x: Parse(s), y: Parse(tfY.Text), z: Parse(tfZ.Text)));
            tfY.OnChanged = s =>
                bind.Set(new Vec3(x: Parse(tfX.Text), y: Parse(s), z: Parse(tfZ.Text)));
            tfZ.OnChanged = s =>
                bind.Set(new Vec3(x: Parse(tfX.Text), y: Parse(tfY.Text), z: Parse(s)));

            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 6f
                    ),
                    child: new Column {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Start,
                        Children = {
                            new Label(
                                text: label,
                                fontSize: theme.FontSizeCaption,
                                color: theme.Hint
                            ),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlignment = MainAxisAlignment.Start,
                                CrossAxisAlignment = CrossAxisAlignment.Center,
                                Children = {
                                    new Label(
                                        text: "X",
                                        fontSize: theme.FontSizeCaption,
                                        color: theme.Accent
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(width: 64f, child: tfX),
                                    new SizedBox(8f),
                                    new Label(
                                        text: "Y",
                                        fontSize: theme.FontSizeCaption,
                                        color: theme.Success
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(width: 64f, child: tfY),
                                    new SizedBox(8f),
                                    new Label(
                                        text: "Z",
                                        fontSize: theme.FontSizeCaption,
                                        color: theme.Primary
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(width: 64f, child: tfZ),
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
            var tfR = MiniFloat(val: current.X.ToString("F2"), theme: theme);
            var tfG = MiniFloat(val: current.Y.ToString("F2"), theme: theme);
            var tfB = MiniFloat(val: current.Z.ToString("F2"), theme: theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s: s,
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out float v
                )
                    ? v
                    : 0f;
            }

            tfR.OnChanged = s =>
                bind.Set(new Vec3(x: Parse(s), y: Parse(tfG.Text), z: Parse(tfB.Text)));
            tfG.OnChanged = s =>
                bind.Set(new Vec3(x: Parse(tfR.Text), y: Parse(s), z: Parse(tfB.Text)));
            tfB.OnChanged = s =>
                bind.Set(new Vec3(x: Parse(tfR.Text), y: Parse(tfG.Text), z: Parse(s)));

            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 6f
                    ),
                    child: new Column {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Start,
                        Children = {
                            new Label(
                                text: label,
                                fontSize: theme.FontSizeCaption,
                                color: theme.Hint
                            ),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlignment = MainAxisAlignment.Start,
                                CrossAxisAlignment = CrossAxisAlignment.Center,
                                Children = {
                                    new Label(
                                        text: "R",
                                        fontSize: theme.FontSizeCaption,
                                        color: new Color(r: 0.9f, g: 0.35f, b: 0.35f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfR),
                                    new SizedBox(6f),
                                    new Label(
                                        text: "G",
                                        fontSize: theme.FontSizeCaption,
                                        color: new Color(r: 0.3f, g: 0.85f, b: 0.3f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfG),
                                    new SizedBox(6f),
                                    new Label(
                                        text: "B",
                                        fontSize: theme.FontSizeCaption,
                                        color: new Color(r: 0.3f, g: 0.55f, b: 1.0f)
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
            var tfR = MiniFloat(val: current.X.ToString("F2"), theme: theme);
            var tfG = MiniFloat(val: current.Y.ToString("F2"), theme: theme);
            var tfB = MiniFloat(val: current.Z.ToString("F2"), theme: theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s: s,
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out float v
                )
                    ? v
                    : 0f;
            }

            tfR.OnChanged = s =>
                setter(new Vec3(x: Parse(s), y: Parse(tfG.Text), z: Parse(tfB.Text)));
            tfG.OnChanged = s =>
                setter(new Vec3(x: Parse(tfR.Text), y: Parse(s), z: Parse(tfB.Text)));
            tfB.OnChanged = s =>
                setter(new Vec3(x: Parse(tfR.Text), y: Parse(tfG.Text), z: Parse(s)));

            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 6f
                    ),
                    child: new Column {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Start,
                        Children = {
                            new Label(
                                text: label,
                                fontSize: theme.FontSizeCaption,
                                color: theme.Hint
                            ),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlignment = MainAxisAlignment.Start,
                                CrossAxisAlignment = CrossAxisAlignment.Center,
                                Children = {
                                    new Label(
                                        text: "R",
                                        fontSize: theme.FontSizeCaption,
                                        color: new Color(r: 0.9f, g: 0.35f, b: 0.35f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfR),
                                    new SizedBox(6f),
                                    new Label(
                                        text: "G",
                                        fontSize: theme.FontSizeCaption,
                                        color: new Color(r: 0.3f, g: 0.85f, b: 0.3f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfG),
                                    new SizedBox(6f),
                                    new Label(
                                        text: "B",
                                        fontSize: theme.FontSizeCaption,
                                        color: new Color(r: 0.3f, g: 0.55f, b: 1.0f)
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
            var tfX = MiniFloat(val: current.X.ToString("F2"), theme: theme);
            var tfY = MiniFloat(val: current.Y.ToString("F2"), theme: theme);
            var tfZ = MiniFloat(val: current.Z.ToString("F2"), theme: theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s: s,
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out float v
                )
                    ? v
                    : 0f;
            }

            tfX.OnChanged = s =>
                setter(new Vec3(x: Parse(s), y: Parse(tfY.Text), z: Parse(tfZ.Text)));
            tfY.OnChanged = s =>
                setter(new Vec3(x: Parse(tfX.Text), y: Parse(s), z: Parse(tfZ.Text)));
            tfZ.OnChanged = s =>
                setter(new Vec3(x: Parse(tfX.Text), y: Parse(tfY.Text), z: Parse(s)));

            return new PropRow(
                new Padding(
                    padding: new EdgeInsets(
                        left: 0f,
                        top: 0f,
                        right: 0f,
                        bottom: 6f
                    ),
                    child: new Column {
                        MainAxisAlignment = MainAxisAlignment.Start,
                        CrossAxisAlignment = CrossAxisAlignment.Start,
                        Children = {
                            new Label(
                                text: label,
                                fontSize: theme.FontSizeCaption,
                                color: theme.Hint
                            ),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlignment = MainAxisAlignment.Start,
                                CrossAxisAlignment = CrossAxisAlignment.Center,
                                Children = {
                                    new Label(
                                        text: "X",
                                        fontSize: theme.FontSizeCaption,
                                        color: theme.Accent
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(width: 64f, child: tfX),
                                    new SizedBox(8f),
                                    new Label(
                                        text: "Y",
                                        fontSize: theme.FontSizeCaption,
                                        color: theme.Success
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(width: 64f, child: tfY),
                                    new SizedBox(8f),
                                    new Label(
                                        text: "Z",
                                        fontSize: theme.FontSizeCaption,
                                        color: theme.Primary
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(width: 64f, child: tfZ),
                                },
                            },
                        },
                    }
                )
            );
        }

        private static AdwEntry MiniFloat(string val, ThemeData theme) => new() {
            Text = val,
            Width = 64f,
            Compact = true,
        };

        public override Size Measure(Constraints c)
        {
            _size = _inner.Measure(c);
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
            _inner.Layout(origin);
        }

        public override void Paint(PaintList paint) => _inner.Paint(paint);

        public override Widget? HitTest(Offset point)
        {
            if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
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
            _size = c.Constrain(new Size(width: c.MaxWidth, height: 26f));
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
        }

        public override void Paint(PaintList paint)
        {
            if (_hovered)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X,
                        y: Bounds.Y,
                        width: Bounds.Width,
                        height: Bounds.Height - 1f
                    ),
                    color: _theme.ControlHover,
                    radius: 4f
                );
            }

            // Disclosure chevron — ▾ expanded, ▸ collapsed.
            const float cs = 14f;
            string chevron = _collapsed ? Icons.ChevronRight : Icons.ChevronDown;
            Icons.Draw(
                paint: paint,
                glyph: chevron,
                box: new Rect(
                    x: Bounds.X,
                    y: Bounds.Y,
                    width: cs,
                    height: Bounds.Height
                ),
                color: _theme.TextSecondary,
                size: cs
            );

            float fs = _theme.FontSizeCaption;
            float ty = Bounds.Y + ((Bounds.Height - fs) / 2f) + (fs * 0.8f);
            paint.AddText(
                text: _title,
                baselineX: Bounds.X + cs + 2f,
                baselineY: ty,
                color: _theme.OnSurface,
                fontSize: fs,
                fontWeight: FontWeight.SemiBold
            );

            // Full-width hairline closing the header band off from the rows below.
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: Bounds.Bottom - 1f,
                    width: Bounds.Width,
                    height: 1f
                ),
                color: _theme.Separator
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
            if (Bounds.Contains(px: point.X, py: point.Y)) _onToggle();
        }
    }
}
