using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.Editor.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
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

    /// <summary>Sidebar glyphs, positionally matched to <see cref="_sections" />.</summary>
    private readonly string[] _sectionIcons = [
        Icons.Tune, Icons.Palette, Icons.Description, Icons.Code, Icons.Dashboard,
        Icons.Terminal, Icons.Bolt,
    ];

    private readonly string[] _sections =
        ["General", "Appearance", "UI Font", "Editor Font", "Panels", "Terminal", "Developer"];

    private readonly AdwSidebar _sidebar;

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
                title: null,
                items: [
                    .. _sections.Select((s, i) => new AdwSidebarItem(
                            title: s,
                            iconName: _sectionIcons[i]
                        )
                    ),
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
        SwapChild(previous: previous, next: _content);
        RequestLayout();
    }

    // ── Tree ──────────────────────────────────────────────────────────────────

    private Widget Build()
    {
        bool searching = !string.IsNullOrWhiteSpace(_search);
        _sidebar.Selected = _selected;

        var rows = BuildRows();
        var page = new AdwPreferencesPage();

        if (searching)
        {
            // Search spans every section, so each one that still has a match becomes its own group
            // — the same page shape, filtered, rather than a separate results list.
            string q = _search.Trim();
            foreach (string section in _sections)
            {
                var matches = rows.Where(r =>
                    r.Section == section &&
                    (r.Title.Contains(
                         value: q,
                         comparisonType: StringComparison.OrdinalIgnoreCase
                     ) ||
                     (r.Desc?.Contains(
                         value: q,
                         comparisonType: StringComparison.OrdinalIgnoreCase
                     ) ?? false) ||
                     r.Section.Contains(
                         value: q,
                         comparisonType: StringComparison.OrdinalIgnoreCase
                     ))
                ).ToList();
                if (matches.Count > 0) page.Groups.Add(Group(title: section, rows: matches));
            }

            if (page.Groups.Count == 0)
            {
                page.Groups.Add(
                    new AdwStatusPage {
                        IconName = Icons.Search,
                        Title = "No Results Found",
                        Description = $"No settings match “{q}”.",
                        Compact = true,
                    }
                );
            }
        }
        else
        {
            string section = _sections[_selected];
            page.Groups.Add(
                Group(title: section, rows: rows.Where(r => r.Section == section).ToList())
            );
        }

        var split = new AdwNavigationSplitView {
            Sidebar = new AdwToolbarView(_sidebar) {
                TopBars = {
                    new AdwHeaderBar {
                        Title = "Settings",
                        ShowEndWindowControls = false,
                    },
                },
            },
            // AdwPreferencesPage scrolls itself; wrapping it in a ScrollView kills both.
            Content = new AdwToolbarView(page) {
                TopBars = {
                    new AdwHeaderBar {
                        TitleWidget = new AdwClamp(child: _searchField, maximumSize: 360f),
                        ShowStartWindowControls = false,
                    },
                },
            },
        };
        return new ColoredBox(color: _theme.Window, child: split);
    }

    /// <summary>One boxed list: the section's rows, each control hung off its row's end.</summary>
    private static AdwPreferencesGroup Group(string title, IReadOnlyList<RowDef> rows)
    {
        var group = new AdwPreferencesGroup(title);
        foreach (var row in rows)
        {
            group.Rows.Add(
                new AdwActionRow(title: row.Title, subtitle: row.Desc) {
                    Suffixes = { row.Control() },
                }
            );
        }

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
                Section: "General",
                Title: "Reopen last project",
                Desc: "Open the most recent project on launch instead of the welcome screen.",
                Control: () => new AdwSwitch(
                    value: settings.ReopenLastProject.Value,
                    onChanged: v => settings.ReopenLastProject.Value = v
                )
            )
        );
        if (OperatingSystem.IsMacOS())
        {
            rows.Add(
                new RowDef(
                    Section: "General",
                    Title: "Native menu bar",
                    Desc:
                    "Use the macOS system menu bar; off shows the in-window menu bar instead.",
                    Control: () => new AdwSwitch(
                        value: settings.NativeMenuBar.Value,
                        onChanged: v => settings.NativeMenuBar.Value = v
                    )
                )
            );
        }

        rows.Add(
            new RowDef(
                Section: "General",
                Title: "Recent projects",
                Desc: $"{history.Recent.Value.Length} entries in the File ▸ Open Recent menu.",
                Control: () => new AdwButton(
                    label: "Clear",
                    onPressed: () =>
                    {
                        history.ClearRecent();
                        Rebuild();
                    }
                )
            )
        );
        rows.Add(
            new RowDef(
                Section: "General",
                Title: "Settings database",
                Desc: EditorSettings.DbPath,
                Control: () => new AdwButton(
                    label: "Copy Path",
                    onPressed: () =>
                    {
                        _prefs.App.Engine.SetClipboard(EditorSettings.DbPath);
                        Owner?.ShowSnackbar("Settings path copied");
                    }
                )
            )
        );
        rows.Add(
            new RowDef(
                Section: "General",
                Title: "Reset settings",
                Desc: "Restore every editor setting to its default. Recent projects are kept.",
                Control: () => new AdwButton(
                    label: "Reset All",
                    onPressed: () =>
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
                Section: "Appearance",
                Title: "Theme mode",
                Desc: "System follows the OS light/dark appearance; changes apply immediately.",
                Control: () =>
                {
                    string[] labels = ["System", "Dark", "Light"];
                    return new AdwDropDown(
                        items: labels,
                        selectedIndex: (int)settings.ThemeMode.Value,
                        onSelected: i => settings.ThemeMode.Value = (EditorThemeMode)i
                    );
                }
            )
        );

        // UI Font
        rows.Add(
            new RowDef(
                Section: "UI Font",
                Title: "UI font family",
                Desc: "Face used by all interface text (swaps the \"Inter\" family live).",
                Control: () => FontDropdown(
                    currentPath: settings.UiFontPath.Value,
                    defaultLabel: "Inter (default)",
                    apply: p => settings.UiFontPath.Value = p
                )
            )
        );
        rows.Add(
            new RowDef(
                Section: "UI Font",
                Title: "UI font size",
                Desc:
                "Base interface font size in points (default 13). Scales titles and captions with it.",
                Control: () => NumberBox(
                    value: settings.UiFontSize.Value,
                    min: 9f,
                    max: 26f,
                    step: 1f,
                    apply: v => settings.UiFontSize.Value = v
                )
            )
        );

        // Editor Font
        rows.Add(
            new RowDef(
                Section: "Editor Font",
                Title: "Editor font family",
                Desc:
                "Monospace face for the code editor and console (swaps the \"code\" family live).",
                Control: () => FontDropdown(
                    currentPath: settings.EditorFontPath.Value,
                    defaultLabel: "Iosevka (default)",
                    apply: p => settings.EditorFontPath.Value = p
                )
            )
        );
        rows.Add(
            new RowDef(
                Section: "Editor Font",
                Title: "Editor font size",
                Desc: "Code editor font size in points (default 13).",
                Control: () => NumberBox(
                    value: settings.EditorFontSize.Value,
                    min: 8f,
                    max: 32f,
                    step: 1f,
                    apply: v => settings.EditorFontSize.Value = v
                )
            )
        );

        // Panels
        if (layout?.Dock is { } dock)
        {
            foreach (var panel in dock.Panels.OrderBy(p => p.Title))
            {
                string id = panel.PanelId;
                rows.Add(
                    new RowDef(
                        Section: "Panels",
                        Title: panel.Title,
                        Desc: $"Show the {panel.Title} panel in the dock.",
                        Control: () => new AdwSwitch(
                            value: dock.IsPanelOpen(id),
                            onChanged: v =>
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
                    Section: "Panels",
                    Title: "Reset layout",
                    Desc: "Restore the default dock arrangement.",
                    Control: () => new AdwButton(
                        label: "Reset",
                        onPressed: () =>
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
                    Section: "Panels",
                    Title: "No project open",
                    Desc: "Open a project to configure its panel layout.",
                    Control: () => new SizedBox()
                )
            );
        }

        // Terminal (console)
        rows.Add(
            new RowDef(
                Section: "Terminal",
                Title: "Console font size",
                Desc: "Log text size in points (default follows the theme caption size).",
                Control: () => NumberBox(
                    value: settings.ConsoleFontSize.Value > 0
                        ? settings.ConsoleFontSize.Value
                        : _theme.FontSizeCaption,
                    min: 8f,
                    max: 24f,
                    step: 1f,
                    apply: v => settings.ConsoleFontSize.Value = v
                )
            )
        );
        rows.Add(
            new RowDef(
                Section: "Terminal",
                Title: "Clear on play",
                Desc: "Empty the console every time play mode starts.",
                Control: () => new AdwSwitch(
                    value: settings.ConsoleClearOnPlay.Value,
                    onChanged: v => settings.ConsoleClearOnPlay.Value = v
                )
            )
        );

        // Developer
        rows.Add(
            new RowDef(
                Section: "Developer",
                Title: "Reduced editor graphics",
                Desc: "Disable TAA/bloom/SSR/DoF while authoring; play mode always renders full.",
                Control: () => new AdwSwitch(
                    value: settings.ReducedEditorGraphics.Value,
                    onChanged: v => settings.ReducedEditorGraphics.Value = v
                )
            )
        );
        rows.Add(
            new RowDef(
                Section: "Developer",
                Title: "VSync",
                Desc: "Cap presentation to the display refresh rate.",
                Control: () => new AdwSwitch(
                    value: settings.VSync.Value,
                    onChanged: v => settings.VSync.Value = v
                )
            )
        );
        rows.Add(
            new RowDef(
                Section: "Developer",
                Title: "Partial repaint",
                Desc: "Redraw only damaged regions on idle frames (GPU-scissor).",
                Control: () => new AdwSwitch(
                    value: _prefs.App.PartialRepaintEnabled,
                    onChanged: v => _prefs.App.PartialRepaintEnabled = v
                )
            )
        );
        rows.Add(
            new RowDef(
                Section: "Developer",
                Title: "Continuous render",
                Desc: "Render every frame even when idle (throughput testing; burns CPU/GPU).",
                Control: () => new AdwSwitch(
                    value: _prefs.App.ForceContinuousRender,
                    onChanged: v => _prefs.App.ForceContinuousRender = v
                )
            )
        );
        if (FileDialog.PlatformSupported)
        {
            rows.Add(
                new RowDef(
                    Section: "Developer",
                    Title: "Native file dialogs",
                    Desc: "Use the OS open/save dialogs; off uses the in-app picker everywhere.",
                    Control: () => new AdwSwitch(
                        value: settings.NativeFileDialogs.Value,
                        onChanged: v => settings.NativeFileDialogs.Value = v
                    )
                )
            );
        }

        rows.Add(
            new RowDef(
                Section: "Developer",
                Title: "Window chrome",
                Desc:
                "Titlebar style for ALL app windows (main, Settings, dialogs). Auto follows " +
                "the OS: macOS unified traffic lights, GNOME Adwaita buttons, system " +
                "decorations elsewhere (Windows/KDE); override to test any look. Native OS " +
                "file panels keep their own chrome.",
                Control: () =>
                {
                    string[] modes = ["auto", "system", "mac", "adwaita"];
                    string[] labels = ["Auto", "System", "macOS Unified", "GNOME (Adwaita)"];
                    int sel = Array.IndexOf(array: modes, value: settings.WindowChromeMode.Value);
                    if (sel < 0) sel = 0;
                    return new AdwDropDown(
                        items: labels,
                        selectedIndex: sel,
                        onSelected: i => settings.WindowChromeMode.Value = modes[i]
                    );
                }
            )
        );
        // GPU picker — only worth showing when the machine actually has a choice. The same card can
        // be listed once per graphics API, so the entries name both (see GpuInfo.DisplayName).
        var gpus = _prefs.App.Engine.EnumerateGpus();
        if (gpus.Count > 1)
        {
            rows.Add(
                new RowDef(
                    Section: "Developer",
                    Title: "Graphics device",
                    Desc:
                    "Which GPU the editor renders on. Automatic picks the fastest one. Takes " +
                    "effect after restarting the editor — the GPU device is created once at " +
                    "startup. ZIGOTE_GPU / ZIGOTE_GPU_POWER override this for a single launch.",
                    Control: () =>
                    {
                        var active = _prefs.App.Engine.ActiveGpu();
                        string[] labels = [
                            active is { } a ? $"Automatic (now: {a.Name})" : "Automatic",
                            .. gpus.Select(g => g.DisplayName),
                        ];
                        // Entry 0 is Automatic, so a stored index shifts by one.
                        int stored = settings.GpuIndex.Value;
                        int sel = stored >= 0 && stored < gpus.Count ? stored + 1 : 0;
                        return new AdwDropDown(
                            items: labels,
                            selectedIndex: sel,
                            onSelected: i => settings.GpuIndex.Value = i - 1
                        );
                    }
                )
            );
        }

        rows.Add(
            new RowDef(
                Section: "Developer",
                Title: "Debug menu",
                Desc: "The in-engine diagnostics overlay (Shift+D in any window).",
                Control: () => new AdwButton(
                    label: "Open",
                    onPressed: () => _prefs.App.ToggleDebugPanel()
                )
            )
        );

        return rows;
    }

    private Widget FontDropdown(string? currentPath, string defaultLabel, Action<string?> apply)
    {
        var fonts = EditorPreferences.AvailableFonts();
        string[] labels = new string[fonts.Count + 1];
        labels[0] = defaultLabel;
        int sel = 0;
        for (int i = 0; i < fonts.Count; i++)
        {
            labels[i + 1] = fonts[i].Name;
            if (currentPath is not null &&
                string.Equals(
                    a: fonts[i].Path,
                    b: currentPath,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                ))
                sel = i + 1;
        }

        return new AdwDropDown(
            items: labels,
            selectedIndex: sel,
            onSelected: i => apply(i == 0 ? null : fonts[i - 1].Path)
        );
    }

    private static Widget NumberBox(float value, float min, float max, float step,
        Action<float> apply)
    {
        return new AdwSpinButton(
            value: value,
            min: min,
            max: max,
            step: step,
            onChanged: v => apply((float)v)
        );
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        _content.Measure(Constraints.Tight(width: _size.Width, height: _size.Height));
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

    public override Widget? HitTest(Offset point) => !Bounds.Contains(px: point.X, py: point.Y)
        ? null
        : _content.HitTest(point);

    public override IEnumerable<Widget> GetChildren() => [_content];

    private readonly record struct RowDef(
        string Section,
        string Title,
        string? Desc,
        Func<Widget> Control);
}
