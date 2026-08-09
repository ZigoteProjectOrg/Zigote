using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.Editor.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Settings;

/// <summary>
///     The editor Settings window content (hosted in its own OS window — see
///     <see cref="SettingsWindowHost" />): GNOME's preferences shape — a header bar carrying the
///     search entry, a navigation sidebar of sections (General / Appearance / UI Font / Editor Font
///     / Panels / Terminal / Developer), and a preferences page of boxed-list rows beside it. Every
///     control writes an <see cref="EditorSettings" /> preference — persistence (SQLite) happens
///     inside the preference and the reactive appliers in <see cref="EditorPreferences" /> push the
///     change into the running editor, so a row never applies anything by hand. "Reset All" is the
///     group's batched <c>Reset()</c>.
/// </summary>
public sealed class SettingsWindow : Widget
{
    private readonly Func<EditorLayout?> _layout;
    private readonly EditorPreferences _prefs;
    private readonly AdwSearchEntry _searchField;
    private readonly AdwSidebar _sidebar;

    private readonly string[] _sections =
        ["General", "Appearance", "UI Font", "Editor Font", "Panels", "Terminal", "Developer"];

    /// <summary>Sidebar glyphs, positionally matched to <see cref="_sections" />.</summary>
    private readonly string[] _sectionIcons = [
        Icons.Tune, Icons.Palette, Icons.Description, Icons.Code, Icons.Dashboard,
        Icons.Terminal, Icons.Bolt,
    ];

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
        // Both are retained across rebuilds: typing must never lose focus/caret state, and the
        // sidebar owns the selected index it would otherwise be handed back every rebuild.
        _searchField = new AdwSearchEntry { Placeholder = "Search settings" };
        _searchField.OnChanged = s =>
        {
            _search = s;
            Rebuild();
        };
        _sidebar = new AdwSidebar(
            new AdwSidebarSection(
                null,
                [
                    .. _sections.Select((s, i) => new AdwSidebarItem(s, _sectionIcons[i])),
                ]
            )
        ) {
            OnSelected = i =>
            {
                _selected = i;
                _searchField.Text = string.Empty;
                _search = string.Empty;
                Rebuild();
            },
        };
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
        // SwapChild, not a bare attach: the outgoing content used to be left mounted forever — still
        // owning its effects and tickers — because nothing ever detached it.
        var previous = _content;
        _content = Build();
        SwapChild(previous, _content);
        RequestLayout();
    }

    // ── Tree ──────────────────────────────────────────────────────────────────

    private Widget Build()
    {
        var searching = !string.IsNullOrWhiteSpace(_search);
        _sidebar.Selected = _selected;

        var rows = BuildRows();
        var page = new AdwPreferencesPage();

        if (searching)
        {
            // Search spans every section, so each one that still has a match becomes its own group
            // — the same page shape, filtered, rather than a separate results list.
            var q = _search.Trim();
            foreach (var section in _sections)
            {
                var matches = rows.Where(r =>
                    r.Section == section &&
                    (r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                     (r.Desc?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                     r.Section.Contains(q, StringComparison.OrdinalIgnoreCase))
                ).ToList();
                if (matches.Count > 0) page.Groups.Add(Group(section, matches));
            }

            if (page.Groups.Count == 0)
                page.Groups.Add(
                    new AdwStatusPage {
                        IconName = Icons.Search,
                        Title = "No Results Found",
                        Description = $"No settings match “{q}”.",
                        Compact = true,
                    }
                );
        }
        else
        {
            var section = _sections[_selected];
            page.Groups.Add(Group(section, rows.Where(r => r.Section == section).ToList()));
        }

        var split = new AdwNavigationSplitView {
            Sidebar = new AdwToolbarView(_sidebar) {
                TopBars = { new AdwHeaderBar { Title = "Settings", ShowEndWindowControls = false } },
            },
            Content = new AdwToolbarView(new ScrollView(page)) {
                TopBars = {
                    new AdwHeaderBar {
                        TitleWidget = new AdwClamp(_searchField, 360f),
                        ShowStartWindowControls = false,
                    },
                },
            },
        };
        return new ColoredBox(_theme.Window, split);
    }

    /// <summary>One boxed list: the section's rows, each control hung off its row's end.</summary>
    private static AdwPreferencesGroup Group(string title, IReadOnlyList<RowDef> rows)
    {
        var group = new AdwPreferencesGroup(title);
        foreach (var row in rows)
            group.Rows.Add(
                new AdwActionRow(row.Title, row.Desc) { Suffixes = { row.Control() } }
            );
        return group;
    }

    // ── Rows ──────────────────────────────────────────────────────────────────

    private List<RowDef> BuildRows()
    {
        var history = _prefs.History;
        var settings = _prefs.Settings;
        var layout = _layout();
        var rows = new List<RowDef>();

        // General
        rows.Add(
            new RowDef(
                "General",
                "Reopen last project",
                "Open the most recent project on launch instead of the welcome screen.",
                () => new AdwSwitch(
                    settings.ReopenLastProject.Value,
                    v => settings.ReopenLastProject.Value = v
                )
            )
        );
        if (OperatingSystem.IsMacOS())
            rows.Add(
                new RowDef(
                    "General",
                    "Native menu bar",
                    "Use the macOS system menu bar; off shows the in-window menu bar instead.",
                    () => new AdwSwitch(
                        settings.NativeMenuBar.Value,
                        v => settings.NativeMenuBar.Value = v
                    )
                )
            );
        rows.Add(
            new RowDef(
                "General",
                "Recent projects",
                $"{history.Recent.Value.Length} entries in the File ▸ Open Recent menu.",
                () => new AdwButton(
                    "Clear",
                    () =>
                    {
                        history.ClearRecent();
                        Rebuild();
                    }
                )
            )
        );
        rows.Add(
            new RowDef(
                "General",
                "Settings database",
                EditorSettings.DbPath,
                () => new AdwButton(
                    "Copy Path",
                    () =>
                    {
                        _prefs.App.Engine.SetClipboard(EditorSettings.DbPath);
                        Owner?.ShowSnackbar("Settings path copied");
                    }
                )
            )
        );
        rows.Add(
            new RowDef(
                "General",
                "Reset settings",
                "Restore every editor setting to its default. Recent projects are kept.",
                () => new AdwButton(
                    "Reset All",
                    () =>
                    {
                        // One batched group reset: the appliers settle once each, then the
                        // shell (and this window, via ApplyTheme) restyle to the defaults.
                        settings.Reset();
                        Rebuild();
                        Owner?.ShowSnackbar("Settings restored to defaults");
                    }
                ) { Style = AdwButtonStyle.Destructive }
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
                    return new AdwDropDown(
                        labels,
                        (int)settings.ThemeMode.Value,
                        i => settings.ThemeMode.Value = (EditorThemeMode)i
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
                () => FontDropdown(
                    settings.UiFontPath.Value,
                    "Inter (default)",
                    p => settings.UiFontPath.Value = p
                )
            )
        );
        rows.Add(
            new RowDef(
                "UI Font",
                "UI font size",
                "Base interface font size in points (default 13). Scales titles and captions with it.",
                () => NumberBox(
                    settings.UiFontSize.Value,
                    9f,
                    26f,
                    1f,
                    v => settings.UiFontSize.Value = v
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
                    settings.EditorFontPath.Value,
                    "Iosevka (default)",
                    p => settings.EditorFontPath.Value = p
                )
            )
        );
        rows.Add(
            new RowDef(
                "Editor Font",
                "Editor font size",
                "Code editor font size in points (default 13).",
                () => NumberBox(
                    settings.EditorFontSize.Value,
                    8f,
                    32f,
                    1f,
                    v => settings.EditorFontSize.Value = v
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
                        () => new AdwSwitch(
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
                    () => new AdwButton(
                        "Reset",
                        () =>
                        {
                            layout.ResetLayout();
                            Rebuild();
                        }
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
                    settings.ConsoleFontSize.Value > 0
                        ? settings.ConsoleFontSize.Value
                        : _theme.FontSizeCaption,
                    8f,
                    24f,
                    1f,
                    v => settings.ConsoleFontSize.Value = v
                )
            )
        );
        rows.Add(
            new RowDef(
                "Terminal",
                "Clear on play",
                "Empty the console every time play mode starts.",
                () => new AdwSwitch(
                    settings.ConsoleClearOnPlay.Value,
                    v => settings.ConsoleClearOnPlay.Value = v
                )
            )
        );

        // Developer
        rows.Add(
            new RowDef(
                "Developer",
                "Reduced editor graphics",
                "Disable TAA/bloom/SSR/DoF while authoring; play mode always renders full.",
                () => new AdwSwitch(
                    settings.ReducedEditorGraphics.Value,
                    v => settings.ReducedEditorGraphics.Value = v
                )
            )
        );
        rows.Add(
            new RowDef(
                "Developer",
                "VSync",
                "Cap presentation to the display refresh rate.",
                () => new AdwSwitch(settings.VSync.Value, v => settings.VSync.Value = v)
            )
        );
        rows.Add(
            new RowDef(
                "Developer",
                "Partial repaint",
                "Redraw only damaged regions on idle frames (GPU-scissor).",
                () => new AdwSwitch(
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
                () => new AdwSwitch(
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
                    () => new AdwSwitch(
                        settings.NativeFileDialogs.Value,
                        v => settings.NativeFileDialogs.Value = v
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
                    var sel = Array.IndexOf(modes, settings.WindowChromeMode.Value);
                    if (sel < 0) sel = 0;
                    return new AdwDropDown(
                        labels,
                        sel,
                        i => settings.WindowChromeMode.Value = modes[i]
                    );
                }
            )
        );
        // GPU picker — only worth showing when the machine actually has a choice. The same card can
        // be listed once per graphics API, so the entries name both (see GpuInfo.DisplayName).
        var gpus = _prefs.App.Engine.EnumerateGpus();
        if (gpus.Count > 1)
            rows.Add(
                new RowDef(
                    "Developer",
                    "Graphics device",
                    "Which GPU the editor renders on. Automatic picks the fastest one. Takes " +
                    "effect after restarting the editor — the GPU device is created once at " +
                    "startup. ZIGOTE_GPU / ZIGOTE_GPU_POWER override this for a single launch.",
                    () =>
                    {
                        var active = _prefs.App.Engine.ActiveGpu();
                        string[] labels = [
                            active is { } a ? $"Automatic (now: {a.Name})" : "Automatic",
                            .. gpus.Select(g => g.DisplayName),
                        ];
                        // Entry 0 is Automatic, so a stored index shifts by one.
                        var stored = settings.GpuIndex.Value;
                        var sel = stored >= 0 && stored < gpus.Count ? stored + 1 : 0;
                        return new AdwDropDown(labels, sel, i => settings.GpuIndex.Value = i - 1);
                    }
                )
            );
        rows.Add(
            new RowDef(
                "Developer",
                "Debug menu",
                "The in-engine diagnostics overlay (Shift+D in any window).",
                () => new AdwButton("Open", () => _prefs.App.ToggleDebugPanel())
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

        return new AdwDropDown(labels, sel, i => apply(i == 0 ? null : fonts[i - 1].Path));
    }

    private static Widget NumberBox(float value, float min, float max, float step,
        Action<float> apply)
    {
        return new AdwSpinButton(
            value,
            min,
            max,
            step,
            v => apply((float)v)
        );
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
}
