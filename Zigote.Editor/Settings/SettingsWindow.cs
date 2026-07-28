using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.Editor.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.Editor.Settings;

/// <summary>
///     The editor Settings window content (hosted in its own OS window — see
///     <see cref="SettingsWindowHost" />): a Zed-style two-pane layout with a searchable section
///     sidebar (General / Appearance / UI Font / Editor Font / Panels / Terminal / Developer) and a
///     scrollable content pane of setting rows. Every control writes through
///     <see cref="EditorPreferences" /> so changes apply live and persist to editor.json.
/// </summary>
public sealed class SettingsWindow : Widget
{
    private const float SidebarW = 220f;
    private const float RowVPad = 10f;

    private static readonly string[] ThemeModes = ["system", "dark", "light"];

    private readonly Func<EditorLayout?> _layout;
    private readonly EditorPreferences _prefs;
    private readonly SearchField _searchField;

    private readonly string[] _sections =
        ["General", "Appearance", "UI Font", "Editor Font", "Panels", "Terminal", "Developer"];

    private Widget _content;
    private string _search = string.Empty;
    private int _selected;
    private Size _size;
    private ThemeData _theme;

    public SettingsWindow(EditorPreferences prefs, Func<EditorLayout?> layout, ThemeData theme)
    {
        _prefs = prefs;
        _layout = layout;
        _theme = theme;
        // Retained across rebuilds so typing in it never loses focus/caret state.
        _searchField = new SearchField(
            "Search settings",
            s =>
            {
                _search = s;
                Rebuild();
            }
        );
        _content = Build();
    }

    /// <summary>Re-style after a theme switch (the host recreates cheaper than re-theming).</summary>
    public void ApplyTheme(ThemeData theme)
    {
        _theme = theme;
        Rebuild();
    }

    private void Rebuild()
    {
        _content = Build();
        if (Owner is not null) _content.Attach(Owner, this);
        RequestLayout();
    }

    // ── Tree ──────────────────────────────────────────────────────────────────

    private Widget Build()
    {
        var searching = !string.IsNullOrWhiteSpace(_search);

        var sidebar = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisAlignment = MainAxisAlignment.Start,
        };
        sidebar.Children.Add(
            new Padding(
                new EdgeInsets(
                    Spacing.Md,
                    Spacing.Md,
                    Spacing.Md,
                    Spacing.Sm
                ),
                new SizedBox(height: 26f, child: _searchField)
            )
        );
        for (var i = 0; i < _sections.Length; i++)
        {
            var idx = i;
            sidebar.Children.Add(
                new SectionRow(
                    _sections[i],
                    _theme,
                    !searching && _selected == i,
                    () =>
                    {
                        _selected = idx;
                        _searchField.Text = string.Empty;
                        _search = string.Empty;
                        Rebuild();
                    }
                )
            );
        }

        var rows = BuildRows();
        var content = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };

        if (searching)
        {
            var q = _search.Trim();
            var any = false;
            foreach (var section in _sections)
            {
                var matches = rows.Where(r =>
                    r.Section == section &&
                    (r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                     (r.Desc?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                     r.Section.Contains(q, StringComparison.OrdinalIgnoreCase))
                ).ToList();
                if (matches.Count == 0) continue;
                any = true;
                AddSectionHeader(content, section);
                foreach (var r in matches) AddRow(content, r);
            }

            if (!any)
                content.Children.Add(
                    new Padding(
                        EdgeInsets.All(Spacing.Lg),
                        new Label($"No settings match \"{q}\"", _theme.FontSizeBody, _theme.Hint)
                    )
                );
        }
        else
        {
            var section = _sections[_selected];
            AddSectionHeader(content, section);
            foreach (var r in rows.Where(r => r.Section == section)) AddRow(content, r);
        }

        return new ColoredBox(
            _theme.Window,
            new Row {
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
                Children = {
                    new SizedBox(SidebarW, child: new ColoredBox(_theme.Sidebar, sidebar)),
                    new SizedBox(1f, child: new ColoredBox(_theme.Separator)),
                    new Expanded(
                        new ScrollView(
                            new Padding(
                                new EdgeInsets(
                                    Spacing.Xl,
                                    Spacing.Lg,
                                    Spacing.Xl,
                                    Spacing.Xl
                                ),
                                content
                            )
                        )
                    ),
                },
            }
        );
    }

    private void AddSectionHeader(Column content, string section)
    {
        content.Children.Add(
            new Padding(
                EdgeInsets.Only(bottom: Spacing.Sm),
                new Label(section, _theme.FontSizeTitle, _theme.OnSurface) {
                    FontWeight = FontWeight.SemiBold,
                }
            )
        );
    }

    private void AddRow(Column content, RowDef row)
    {
        var text = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
        };
        text.Children.Add(new Label(row.Title, _theme.FontSizeBody, _theme.OnSurface));
        if (row.Desc is not null)
            text.Children.Add(
                new Padding(
                    EdgeInsets.Only(top: 2f),
                    new Label(row.Desc, _theme.FontSizeCaption, _theme.Hint)
                )
            );

        content.Children.Add(
            new Padding(
                EdgeInsets.Symmetric(0f, RowVPad),
                new Row {
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = {
                        new Expanded(text),
                        new SizedBox(Spacing.Md),
                        row.Control(),
                    },
                }
            )
        );
        content.Children.Add(new SizedBox(height: 1f, child: new ColoredBox(_theme.Separator)));
    }

    // ── Rows ──────────────────────────────────────────────────────────────────

    private List<RowDef> BuildRows()
    {
        var config = _prefs.Config;
        var layout = _layout();
        var rows = new List<RowDef>();

        // General
        rows.Add(
            new RowDef(
                "General",
                "Reopen last project",
                "Open the most recent project on launch instead of the welcome screen.",
                () => new Switch(
                    config.ReopenLastProject,
                    v =>
                    {
                        config.ReopenLastProject = v;
                        config.Save();
                    }
                )
            )
        );
        if (OperatingSystem.IsMacOS())
            rows.Add(
                new RowDef(
                    "General",
                    "Native menu bar",
                    "Use the macOS system menu bar; off shows the in-window menu bar instead.",
                    () => new Switch(
                        config.NativeMenuBar,
                        v => _prefs.SetNativeMenuBar(v)
                    )
                )
            );
        rows.Add(
            new RowDef(
                "General",
                "Recent projects",
                $"{config.RecentProjects.Count} entries in the File ▸ Open Recent menu.",
                () => new SizedBox(
                    height: 24f,
                    child: new Button(
                        "Clear",
                        () =>
                        {
                            config.RecentProjects.Clear();
                            config.Save();
                            Rebuild();
                        }
                    )
                )
            )
        );
        rows.Add(
            new RowDef(
                "General",
                "Settings file",
                EditorConfig.FilePath,
                () => new SizedBox(
                    height: 24f,
                    child: new Button(
                        "Copy Path",
                        () =>
                        {
                            _prefs.App.Engine.SetClipboard(EditorConfig.FilePath);
                            Owner?.ShowSnackbar("Settings path copied");
                        }
                    )
                )
            )
        );

        // Appearance
        rows.Add(
            new RowDef(
                "Appearance",
                "Theme mode",
                "System follows the OS light/dark appearance; changes apply immediately.",
                () =>
                {
                    string[] labels = ["System", "Dark", "Light"];
                    var sel = Math.Max(0, Array.IndexOf(ThemeModes, config.ThemeMode));
                    return new SizedBox(
                        140f,
                        26f,
                        new Dropdown<string>(
                            labels,
                            sel,
                            s => s,
                            (i, _) => _prefs.SetThemeMode(ThemeModes[i])
                        )
                    );
                }
            )
        );

        // UI Font
        rows.Add(
            new RowDef(
                "UI Font",
                "UI font family",
                "Face used by all interface text (swaps the \"Inter\" family live).",
                () => FontDropdown(config.UiFontPath, "Inter (default)", p => _prefs.SetUiFont(p))
            )
        );
        rows.Add(
            new RowDef(
                "UI Font",
                "UI font size",
                "Base interface font size in points (default 13). Scales titles and captions with it.",
                () => NumberBox(
                    config.UiFontSize,
                    9f,
                    26f,
                    1f,
                    v => _prefs.SetUiFontSize(v)
                )
            )
        );

        // Editor Font
        rows.Add(
            new RowDef(
                "Editor Font",
                "Editor font family",
                "Monospace face for the code editor and console (swaps the \"code\" family live).",
                () => FontDropdown(
                    config.EditorFontPath,
                    "Iosevka (default)",
                    p => _prefs.SetEditorFont(p)
                )
            )
        );
        rows.Add(
            new RowDef(
                "Editor Font",
                "Editor font size",
                "Code editor font size in points (default 13).",
                () => NumberBox(
                    config.EditorFontSize,
                    8f,
                    32f,
                    1f,
                    v => _prefs.SetEditorFontSize(v)
                )
            )
        );

        // Panels
        if (layout?.Dock is { } dock)
        {
            foreach (var panel in dock.Panels.OrderBy(p => p.Title))
            {
                var id = panel.PanelId;
                rows.Add(
                    new RowDef(
                        "Panels",
                        panel.Title,
                        $"Show the {panel.Title} panel in the dock.",
                        () => new Switch(
                            dock.IsPanelOpen(id),
                            v =>
                            {
                                if (v) dock.OpenPanel(id);
                                else dock.ClosePanelById(id);
                                Rebuild();
                            }
                        )
                    )
                );
            }

            rows.Add(
                new RowDef(
                    "Panels",
                    "Reset layout",
                    "Restore the default dock arrangement.",
                    () => new SizedBox(
                        height: 24f,
                        child: new Button(
                            "Reset",
                            () =>
                            {
                                layout.ResetLayout();
                                Rebuild();
                            }
                        )
                    )
                )
            );
        }
        else
        {
            rows.Add(
                new RowDef(
                    "Panels",
                    "No project open",
                    "Open a project to configure its panel layout.",
                    () => new SizedBox()
                )
            );
        }

        // Terminal (console)
        rows.Add(
            new RowDef(
                "Terminal",
                "Console font size",
                "Log text size in points (default follows the theme caption size).",
                () => NumberBox(
                    config.ConsoleFontSize > 0 ? config.ConsoleFontSize : _theme.FontSizeCaption,
                    8f,
                    24f,
                    1f,
                    v => _prefs.SetConsoleFontSize(v)
                )
            )
        );
        rows.Add(
            new RowDef(
                "Terminal",
                "Clear on play",
                "Empty the console every time play mode starts.",
                () => new Switch(
                    config.ConsoleClearOnPlay,
                    v =>
                    {
                        config.ConsoleClearOnPlay = v;
                        config.Save();
                    }
                )
            )
        );

        // Developer
        if (layout is not null)
            rows.Add(
                new RowDef(
                    "Developer",
                    "Reduced editor graphics",
                    "Disable TAA/bloom/SSR/DoF while authoring; play mode always renders full.",
                    () => new Switch(
                        layout.State.ReducedEditorGraphics,
                        v => layout.State.ReducedEditorGraphics = v
                    )
                )
            );
        rows.Add(
            new RowDef(
                "Developer",
                "VSync",
                "Cap presentation to the display refresh rate.",
                () => new Switch(_prefs.Config.VSync, v => _prefs.SetVSync(v))
            )
        );
        rows.Add(
            new RowDef(
                "Developer",
                "Partial repaint",
                "Redraw only damaged regions on idle frames (GPU-scissor).",
                () => new Switch(
                    _prefs.App.PartialRepaintEnabled,
                    v => _prefs.App.PartialRepaintEnabled = v
                )
            )
        );
        rows.Add(
            new RowDef(
                "Developer",
                "Continuous render",
                "Render every frame even when idle (throughput testing; burns CPU/GPU).",
                () => new Switch(
                    _prefs.App.ForceContinuousRender,
                    v => _prefs.App.ForceContinuousRender = v
                )
            )
        );
        if (FileDialog.PlatformSupported)
            rows.Add(
                new RowDef(
                    "Developer",
                    "Native file dialogs",
                    "Use the OS open/save dialogs; off uses the in-app picker everywhere.",
                    () => new Switch(
                        config.NativeFileDialogs,
                        v => _prefs.SetNativeFileDialogs(v)
                    )
                )
            );
        rows.Add(
            new RowDef(
                "Developer",
                "Window chrome",
                "Titlebar style for ALL app windows (main, Settings, dialogs). Auto follows " +
                "the OS: macOS unified traffic lights, GNOME Adwaita buttons, system " +
                "decorations elsewhere (Windows/KDE); override to test any look. Native OS " +
                "file panels keep their own chrome.",
                () =>
                {
                    string[] modes = ["auto", "system", "mac", "adwaita"];
                    string[] labels = ["Auto", "System", "macOS Unified", "GNOME (Adwaita)"];
                    var sel = Array.IndexOf(modes, config.WindowChromeMode);
                    if (sel < 0) sel = 0;
                    return new SizedBox(
                        190f,
                        26f,
                        new Dropdown<string>(
                            labels,
                            sel,
                            (i, _) => _prefs.SetWindowChrome(modes[i])
                        )
                    );
                }
            )
        );
        rows.Add(
            new RowDef(
                "Developer",
                "Debug menu",
                "The in-engine diagnostics overlay (Shift+D in any window).",
                () => new SizedBox(
                    height: 24f,
                    child: new Button("Open", () => { _prefs.App.ToggleDebugPanel(); })
                )
            )
        );

        return rows;
    }

    private Widget FontDropdown(string? currentPath, string defaultLabel, Action<string?> apply)
    {
        var fonts = EditorPreferences.AvailableFonts();
        var labels = new string[fonts.Count + 1];
        labels[0] = defaultLabel;
        var sel = 0;
        for (var i = 0; i < fonts.Count; i++)
        {
            labels[i + 1] = fonts[i].Name;
            if (currentPath is not null &&
                string.Equals(fonts[i].Path, currentPath, StringComparison.OrdinalIgnoreCase))
                sel = i + 1;
        }

        return new SizedBox(
            190f,
            26f,
            new Dropdown<string>(
                labels,
                sel,
                s => s,
                (i, _) => apply(i == 0 ? null : fonts[i - 1].Path)
            )
        );
    }

    private static Widget NumberBox(float value, float min, float max, float step,
        Action<float> apply)
    {
        // Wide enough for NumberInput's fixed chrome (scrub grip + ▲/▼) plus the 60px-min field.
        var ni = new NumberInput(
            value,
            step,
            min,
            max
        ) { Decimals = step < 1f ? 1 : 0 };
        ni.OnChanged = apply;
        return new SizedBox(150f, 28f, ni);
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        _content.Measure(Constraints.Tight(_size.Width, _size.Height));
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
        return !Bounds.Contains(point.X, point.Y) ? null : _content.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_content];
    }

    private readonly record struct RowDef(
        string Section,
        string Title,
        string? Desc,
        Func<Widget> Control);

    /// <summary>A sidebar section entry: hover highlight + accent selection pill, Zed-style.</summary>
    private sealed class SectionRow : Widget
    {
        private const float H = 30f;
        private readonly Action _onSelect;
        private readonly bool _selected;
        private readonly ThemeData _theme;
        private readonly string _title;
        private bool _hovered;
        private Size _size;

        public SectionRow(string title, ThemeData theme, bool selected, Action onSelect)
        {
            _title = title;
            _theme = theme;
            _selected = selected;
            _onSelect = onSelect;
        }

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(c.MaxWidth, H));
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
            var pill = new Rect(
                Bounds.X + Spacing.Sm,
                Bounds.Y + 2f,
                Bounds.Width - Spacing.Sm * 2f,
                Bounds.Height - 4f
            );
            if (_selected)
                paint.AddRect(pill, _theme.Accent.WithAlpha(0.22f), Radii.Md);
            else if (_hovered)
                paint.AddRect(pill, _theme.Fill4, Radii.Md);

            paint.AddText(
                _title,
                pill.X + Spacing.Sm,
                Bounds.Y + Bounds.Height * 0.5f + _theme.FontSizeBody * 0.36f,
                _selected ? _theme.OnSurface : _theme.TextSecondary,
                _theme.FontSizeBody
            );
        }

        public override Widget? HitTest(Offset point)
        {
            return Bounds.Contains(point.X, point.Y) ? this : null;
        }

        public override void OnPointerDown(Offset point)
        {
            _onSelect();
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

        public override int DebugStateHash()
        {
            return HashCode.Combine(_hovered, _selected);
        }
    }
}