using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Editor.Export;
using Zigote.Editor.Panels;
using Zigote.Editor.Panels.AssetPreview;
using Zigote.Editor.Scene;
using Zigote.Editor.Settings;
using Zigote.Editor.Shading;
using Zigote.Graphs.Core;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Registry;
using Zigote.Modules.UI.CodeEditor;
using Zigote.Runtime.Scene;
using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Menu;
// CodeEditor: Adwaita has no source view (GtkSourceView has no libadwaita counterpart either),
// so the editor keeps this one Material widget. The rest of the shell is Adwaita.

namespace Zigote.Editor.Widgets;

/// <summary>
///     Editor shell with a dockable panel layout. All panels can be dragged by their
///     header and dropped onto other panels (left / right / top / bottom) to rearrange
///     the layout. Split dividers remain draggable. Each header has a collapse button
///     (shrink the region to a strip) and a maximize button (fill the dock); the viewport
///     also maximizes with F11 for fullscreen testing.
/// </summary>
public sealed class EditorLayout : Widget
{
    private readonly EditorActions _actions;
    private readonly App _app;
    private readonly Widget _root;
    private readonly ThemeData _theme;

    // The docked code editor + its tab descriptor and the file currently loaded into it. Wired in
    // BuildDock; used by the focus-aware Save/Undo/Redo routing below.
    private CodeEditor? _codeEditor;
    private DockPanel? _codePanel;
    private ConsolePanel? _consolePanel;
    private string? _openFilePath;
    private Size _size;

    // Re-reads the play state into the header-bar transport; set by BuildHeaderBar.
    private Action? _syncTransport;

    public EditorLayout(EditorState state, ThemeData theme, App app, EditorActions actions)
    {
        State = state;
        _theme = theme;
        _app = app;
        _actions = actions;
        _root = BuildRoot();
    }

    /// <summary>The live session state (the settings window binds Developer toggles to it).</summary>
    public EditorState State { get; }

    /// <summary>The dock (the settings window's Panels section toggles/queries it).</summary>
    public DockLayout? Dock { get; private set; }

    // ── Focus-aware Save / Undo / Redo ──────────────────────────────────────────
    // ⌘S / ⌘Z / ⌘⇧Z mean "the code file" while the docked editor has focus, and "the scene" otherwise.
    // On macOS the native menu eats these keys (the editor never sees them), so the menu actions must
    // route too — these are wired both to the menu and (via CodeEditor.OnSubmit) to the editor's own keys.

    private bool CodeEditorFocused =>
        _codeEditor != null && ReferenceEquals(objA: _app.FocusedWidget, objB: _codeEditor);

    /// <summary>
    ///     Push the persisted editor-font preferences onto the docked code editor + console: font
    ///     sizes, and — after a "code" face swap — drop the editor's cached native text layouts
    ///     (they embed glyphs shaped with the old face).
    /// </summary>
    public void ApplyEditorFontPreferences(EditorSettings settings)
    {
        if (_codeEditor is { } ce)
        {
            float size = settings.EditorFontSize.Value;
            ce.FontSize = Math.Abs(size - 13f) < 0.01f ? null : size;
            ce.InvalidateTextLayouts();
        }

        if (_consolePanel is { } console)
        {
            console.FontSize = settings.ConsoleFontSize.Value;
            console.MarkNeedsPaint();
        }
    }

    // ── Root builder ──────────────────────────────────────────────────────────

    private Widget BuildRoot()
    {
        var dock = BuildDock();

        // Hand the menus to a native bar where one exists (macOS NSMenu). Everywhere else they
        // collapse into the GNOME primary menu — the ☰ button at the end of the header bar — so the
        // shell is one strip: header bar over dock, the arrangement AdwToolbarView exists for.
        var menus = BuildMenus();
        var header = BuildHeaderBar(NativeMenuBar.TryInstall(menus) ? null : menus);
        var status = new StatusBar(
            state: State,
            theme: _theme,
            paletteHint: MenuAccelerators.Display(PaletteShortcut) ?? PaletteShortcut,
            showConsole: () => dock.OpenPanel("console"),
            showPalette: ShowCommandPalette
        );
        return new AdwToolbarView(dock) { TopBars = { header }, BottomBars = { status } };
    }

    // ── Menu bar ────────────────────────────────────────────────────────────────

    private IReadOnlyList<AppMenu> BuildMenus()
    {
        var recent = _actions.History.Recent.Value
            .Select(p => new ContextMenuItem(
                    Label: Path.GetFileNameWithoutExtension(p),
                    OnSelect: () => _actions.OpenProject(p)
                )
            )
            .ToList();
        if (recent.Count == 0)
            recent.Add(new ContextMenuItem(Label: "(none)", OnSelect: null));

        var file = new AppMenu(
            Title: "File",
            Items: [
                new ContextMenuItem(
                    Label: "New Project",
                    OnSelect: () => ProjectDialogs.ShowNew(app: _app, onOpen: _actions.OpenProject)
                ),
                new ContextMenuItem(
                    Label: "Open Project…",
                    OnSelect: () => ProjectDialogs.ShowOpen(app: _app, onOpen: _actions.OpenProject)
                ),
                new ContextMenuItem(Label: "Open Recent", OnSelect: null, Children: recent),
                new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                new ContextMenuItem(Label: "Save", OnSelect: DoSave, Shortcut: "⌘S"),
                new ContextMenuItem(Label: "Save Scene As…", OnSelect: SaveSceneAs),
                new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                new ContextMenuItem(
                    Label: "Export Game…",
                    OnSelect: () => ExportDialog.Show(app: _app, theme: _theme, state: State)
                ),
                new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                new ContextMenuItem(Label: "Close Project", OnSelect: _actions.CloseProject),
                new ContextMenuItem(Label: "Quit", OnSelect: _actions.Quit, Shortcut: "⌘Q"),
            ]
        );

        var edit = new AppMenu(
            Title: "Edit",
            Items: [
                new ContextMenuItem(Label: "Undo", OnSelect: DoUndo, Shortcut: "⌘Z"),
                new ContextMenuItem(Label: "Redo", OnSelect: DoRedo, Shortcut: "⌘⇧Z"),
                new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                new ContextMenuItem(
                    Label: "Duplicate Node",
                    OnSelect: State.DuplicateSelected,
                    Shortcut: "⌘D"
                ),
                // No accelerator: Delete/Backspace as a menu key equivalent would eat text editing.
                new ContextMenuItem(Label: "Delete Node", OnSelect: State.DeleteSelected),
                new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                // A running game can capture the pointer for mouselook, which hides the cursor and
                // makes every other panel unreachable. Esc in the viewport is the per-session way out
                // (this menu is not clickable while the cursor is captured); this toggle is the
                // standing veto, so a game that re-takes capture every frame can still be stopped.
                new ContextMenuItem(
                    Label: "Allow Mouse Capture in Play",
                    OnSelect: ToggleMouseCapture,
                    Checked: _app.Engine.AllowRelativeMouseMode
                ),
            ]
        );

        // View — the palette, then one entry per dock panel (re-opens a closed one, surfaces a
        // hidden tab), ⌘1…⌘5 for the ones reached for constantly.
        List<ContextMenuItem> viewItems = [
            new(Label: "Command Palette…", OnSelect: ShowCommandPalette, Shortcut: PaletteShortcut),
            new(Label: "", OnSelect: null, Separator: true),
        ];
        foreach (var panel in Dock!.Panels)
        {
            string id = panel.PanelId;
            viewItems.Add(
                new ContextMenuItem(
                    // The code tab is retitled to the open file; the menu names the panel.
                    Label: id == "codeeditor" ? "Code" : panel.Title,
                    OnSelect: () => Dock.OpenPanel(id),
                    Shortcut: Array.IndexOf(array: PanelShortcutOrder, value: id) is >= 0 and var n
                        ? $"⌘{n + 1}"
                        : null
                )
            );
        }

        viewItems.Add(new ContextMenuItem(Label: "", OnSelect: null, Separator: true));
        viewItems.Add(
            new ContextMenuItem(
                Label: "Maximize Viewport",
                OnSelect: () => Dock.ToggleMaximize("viewport"),
                Shortcut: "F11"
            )
        );
        viewItems.Add(new ContextMenuItem(Label: "Reset Layout", OnSelect: ResetLayout));
        var view = new AppMenu(Title: "View", Items: viewItems);

        var run = new AppMenu(
            Title: "Run",
            Items: [
                new ContextMenuItem(Label: "Play / Stop", OnSelect: TogglePlay, Shortcut: "F5"),
                new ContextMenuItem(Label: "Pause / Resume", OnSelect: TogglePause, Shortcut: "F6"),
            ]
        );

        bool nativeBar = NativeMenuBar.Backend is not null && NativeMenuBar.Enabled;
        var shortcutsItem = new ContextMenuItem(
            Label: "Keyboard Shortcuts",
            OnSelect: ShowShortcuts,
            Shortcut: "⌘/"
        );

        // Native bar only: a Window menu with the AppKit role — the OS appends Minimize/Zoom and
        // the live window list. The in-window bar has nothing useful to render there.
        if (nativeBar)
        {
            var window = new AppMenu(Title: "Window", Items: [], Role: AppMenuRole.Window);
            var nativeHelp = new AppMenu(Title: "Help", Items: [shortcutsItem]);
            return [file, edit, view, run, window, nativeHelp];
        }

        // On macOS "About Zigote Editor" lives in the native app menu (NativeMenuBar.AboutRequested);
        // the in-window bar needs its own Help menu to reach the about screen.
        var help = new AppMenu(
            Title: "Help",
            Items: [
                shortcutsItem,
                new ContextMenuItem(
                    Label: "About Zigote Editor",
                    OnSelect: () => NativeMenuBar.AboutRequested?.Invoke()
                ),
            ]
        );
        return [file, edit, view, run, help];
    }

    // ── Command palette + shortcuts ─────────────────────────────────────────────

    private const string PaletteShortcut = "⌘⇧P";

    // Dock panels that get ⌘1…⌘n, in that order.
    private static readonly string[] PanelShortcutOrder =
        ["hierarchy", "viewport", "inspector", "browser", "console"];

    // Viewport keys are handled by the focused panel, not the menu — listed here so the
    // shortcuts window still documents them. GTK accelerator syntax (AdwShortcutLabel).
    private static readonly (string Title, string Accel)[] ViewportShortcuts = [
        ("Move gizmo", "w"),
        ("Rotate gizmo", "e"),
        ("Scale gizmo", "r"),
        ("Frame selection", "f"),
        ("Frame all", "<Shift>f"),
        ("Orbit / fly camera", "c"),
        ("Fly: move", "w a s d"),
        ("Fly: down / up", "q e"),
        ("Delete node", "Delete"),
        ("Rename node (Hierarchy)", "F2"),
    ];

    /// <summary>Every project-relative file under the asset root, capped — the palette's "Asset" rows.</summary>
    private IEnumerable<string> AssetFiles()
    {
        // ponytail: a fresh disk walk per palette open, capped at 5000 files; cache behind the
        // asset watcher if a huge project makes ⌘⇧P feel slow.
        try
        {
            return Directory.Exists(State.AssetRoot)
                ? Directory.EnumerateFiles(
                        path: State.AssetRoot,
                        searchPattern: "*",
                        searchOption: SearchOption.AllDirectories
                    )
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .Take(5000)
                    .ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void ShowCommandPalette()
    {
        List<PaletteCommand> commands = [];

        // Menu actions — the menu model is the single source, so anything added to a menu is
        // searchable (with its shortcut) for free.
        foreach (var menu in BuildMenus())
        {
            foreach (var item in menu.Items)
            {
                if (item.Separator) continue;
                if (item.Children is { } children)
                {
                    foreach (var child in children)
                    {
                        if (child.OnSelect is { } openChild)
                        {
                            commands.Add(
                                new PaletteCommand(
                                    Title: $"{item.Label}: {child.Label}",
                                    Group: menu.Title,
                                    Run: openChild
                                )
                            );
                        }
                    }

                    continue;
                }

                // Disabled placeholders, and the palette itself (no opening it from inside).
                if (item.OnSelect is null || item.Shortcut == PaletteShortcut) continue;
                commands.Add(
                    new PaletteCommand(
                        Title: item.Label.TrimEnd('…'),
                        Group: menu.Title,
                        Run: item.OnSelect!,
                        Accel: MenuAccelerators.Display(item.Shortcut)
                    )
                );
            }
        }

        foreach ((string label, var add) in NodeKinds())
            commands.Add(new PaletteCommand(Title: label, Group: "Add", Run: add));
        commands.Add(
            new PaletteCommand(
                Title: "Settings",
                Group: "Editor",
                Run: () => _actions.OpenSettings?.Invoke()
            )
        );

        foreach (var node in State.Scene.Root.Descendants())
        {
            commands.Add(
                new PaletteCommand(
                    Title: node.Name,
                    Group: "Node",
                    Run: () => State.Select(node)
                )
            );
        }

        foreach (string file in AssetFiles())
        {
            commands.Add(
                new PaletteCommand(
                    Title: Path.GetRelativePath(relativeTo: State.AssetRoot, path: file),
                    Group: "Asset",
                    Run: () =>
                    {
                        Dock?.OpenPanel("browser");
                        State.NotifyAssetSelected(file);
                        State.NotifyOpenFile(file); // text/code opens in the editor; binary is ignored
                    }
                )
            );
        }

        CommandPalette.Show(commands: commands, theme: _theme);
    }

    /// <summary>A menu shortcut ("⌘⇧P", "F5") in the GTK syntax AdwShortcutLabel draws key caps from.</summary>
    private static string GtkAccel(string shortcut)
    {
        if (!MenuAccelerators.TryParse(shortcut: shortcut, chord: out var chord)) return shortcut;
        string s = "";
        if (chord.Modifiers.HasFlag(KeyChord.PlatformCommand)) s += "<Primary>";
        if (chord.Modifiers.HasFlag(Modifiers.Alt)) s += "<Alt>";
        if (chord.Modifiers.HasFlag(Modifiers.Shift)) s += "<Shift>";
        return s + KeyNames.Display(chord.Key);
    }

    private void ShowShortcuts()
    {
        var dialog = new AdwShortcutsDialog();
        foreach (var menu in BuildMenus())
        {
            var items = menu.Items
                .Where(i => i is { Shortcut: not null, Separator: false })
                .Select(i => new AdwShortcutsItem(
                        title: i.Label.TrimEnd('…'),
                        accelerator: GtkAccel(i.Shortcut!)
                    )
                )
                .ToArray();
            if (items.Length > 0) dialog.Add(new AdwShortcutsSection(title: menu.Title, items: items));
        }

        dialog.Add(
            new AdwShortcutsSection(
                title: "Viewport",
                items: ViewportShortcuts
                    .Select(v => new AdwShortcutsItem(title: v.Title, accelerator: v.Accel))
                    .ToArray()
            )
        );
        dialog.Show();
    }

    // ── Transport ───────────────────────────────────────────────────────────────

    private void TogglePlay()
    {
        if (!State.IsPlaying)
        {
            if (State.IsScriptBuilding) return; // StartPlay refuses mid-build; the button is disabled
            if (_actions.Settings.ConsoleClearOnPlay.Value) EditorLog.Clear();
            State.StartPlay();
        }
        else
            State.StopPlay();

        _syncTransport?.Invoke();
    }

    private void TogglePause()
    {
        if (!State.IsPlaying) return;
        State.TogglePause();
        _syncTransport?.Invoke();
    }

    /// <summary>The kinds the header's Add button and the palette both offer.</summary>
    private (string Label, Action Add)[] NodeKinds() => [
        ("Empty Node", () => State.AddNode(name: "Node", kind: NodeKind.Empty)),
        ("Mesh Node", () => State.AddNode(name: "Mesh", kind: NodeKind.Mesh)),
        ("Cube", () =>
            {
                var n = State.AddNode(name: "Cube", kind: NodeKind.Mesh);
                n.MeshPath = "#cube";
                State.NotifySceneChanged();
            }
        ),
        ("Sphere", () =>
            {
                var n = State.AddNode(name: "Sphere", kind: NodeKind.Mesh);
                n.MeshPath = "#sphere";
                State.NotifySceneChanged();
            }
        ),
        ("Light", () => State.AddNode(name: "Light", kind: NodeKind.Light)),
        ("Camera", () =>
            {
                var n = State.AddNode(name: "Camera", kind: NodeKind.Camera);
                n.Position = new Vec3(x: 0, y: 0, z: -3f);
                State.NotifySceneChanged();
            }
        ),
        ("Script Node", () => State.AddNode(name: "Script", kind: NodeKind.Script)),
    ];

    /// <summary>Forbid or re-allow capture entirely. Releases immediately when turning it off.</summary>
    private void ToggleMouseCapture()
    {
        bool allowed = _app.Engine.AllowRelativeMouseMode = !_app.Engine.AllowRelativeMouseMode;
        _app.ShowSnackbar(
            allowed
                ? "Mouse capture allowed — play mode can use free mouselook (Esc releases)."
                : "Mouse capture disabled — the cursor stays visible in play mode."
        );
    }

    private void SaveScene()
    {
        State.Scene.Save(State.ScenePath);
        State.SaveAssets();
        State.SaveProjectSettings(); // persist render settings (minus debug) into the .zigoteproj
        _app.ShowSnackbar($"Saved to {State.ScenePath}");
    }

    private void SaveSceneAs()
    {
        ProjectDialogs.ShowSaveSceneAs(
            app: _app,
            currentPath: State.ScenePath,
            onSave: p =>
            {
                State.ScenePath = p;
                State.Scene.Save(p);
                _app.ShowSnackbar($"Saved to {p}");
            }
        );
    }

    /// <summary>Write the open file back to disk and clear the tab's unsaved indicator.</summary>
    private void SaveOpenFile()
    {
        if (_codeEditor == null) return;
        if (_openFilePath == null)
        {
            _app.ShowSnackbar("No file open to save");
            return;
        }

        try
        {
            File.WriteAllText(path: _openFilePath, contents: _codeEditor.Text);
            Dock?.SetPanelDirty(panelId: "codeeditor", dirty: false);
            _app.ShowSnackbar($"Saved {Path.GetFileName(_openFilePath)}");
        }
        catch (Exception e)
        {
            _app.ShowSnackbar($"Save failed: {e.Message}");
        }
    }

    private void DoSave()
    {
        if (CodeEditorFocused && _openFilePath != null) SaveOpenFile();
        else SaveScene();
    }

    private void DoUndo()
    {
        if (CodeEditorFocused)
        {
            _codeEditor!.Undo();
            return;
        }

        State.History.Undo();
        State.NotifySceneChanged();
    }

    private void DoRedo()
    {
        if (CodeEditorFocused)
        {
            _codeEditor!.Redo();
            return;
        }

        State.History.Redo();
        State.NotifySceneChanged();
    }

    private DockLayout BuildDock()
    {
        // ── Panel content widgets (retained) ──────────────────────────────────
        var hierarchy = new HierarchyPanel(state: State, theme: _theme);
        // Wrap the tree in a scroller and let the panel reveal the selected row into view (the +4 matches
        // the Padding below). A selection made in the viewport now scrolls the hierarchy to it.
        var hierarchyScroll =
            new ScrollView(new Padding(padding: EdgeInsets.All(4f), child: hierarchy));
        hierarchy.OnRevealRequested =
            (top, h) => hierarchyScroll.EnsureVisible(top: top + 4f, height: h);
        var hierarchyPane = new Column {
            Children = {
                new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: 6f, vertical: 4f),
                    child: new AdwSearchEntry {
                        Placeholder = "Filter nodes",
                        OnChanged = f => hierarchy.Filter = f,
                    }
                ),
                new Expanded(hierarchyScroll),
            },
        };
        var viewport = new ViewportPanel(state: State, theme: _theme);
        var inspector = new InspectorPanel(state: State, theme: _theme, app: _app);
        var browser = new AssetBrowserPanel(state: State, theme: _theme);
        var browserScroll = new ScrollView(
            new Padding(padding: EdgeInsets.Symmetric(horizontal: 6f, vertical: 0f), child: browser)
        );
        browser.OnRevealRequested =
            (top, h) => browserScroll.EnsureVisible(top: top, height: h);
        var settings = new SettingsPanel(state: State, theme: _theme);
        var info = new InfoPanel(_theme);
        var assetPreview = new AssetPreviewPanel(state: State, theme: _theme);

        // Code editor: opened from the asset browser via OpenFileRequested (double-click a text/code file).
        // Editable, with ⌘S save + ⌘Z/⌘⇧Z undo/redo; edits mark the tab dirty until saved.
        var codeEditor = new CodeEditor();
        _codeEditor = codeEditor;
        // Any edit marks the tab unsaved; ⌘S (when the editor is focused) writes it back to disk.
        codeEditor.OnChanged += _ =>
        {
            if (_openFilePath != null) Dock?.SetPanelDirty(panelId: "codeeditor", dirty: true);
        };
        codeEditor.OnSubmit = SaveOpenFile;
        State.OpenFileRequested += path =>
        {
            try
            {
                codeEditor.Tokenizer = Highlighting.ForExtension(Path.GetExtension(path));
                codeEditor.Text =
                    File.ReadAllText(path); // setter resets text + undo history (no OnChanged)
                _openFilePath = path;
                if (_codePanel != null) _codePanel.Title = Path.GetFileName(path);
                Dock?.SetPanelDirty(panelId: "codeeditor", dirty: false);
                Dock?.ShowPanel(
                    "codeeditor"
                ); // surface the tab (un-collapse / select / un-maximize)
                _app.RequestFocus(codeEditor); // focus so typing + ⌘S/⌘Z land here
                codeEditor.MarkNeedsLayout();
            }
            catch
            {
                // Unreadable/binary file — leave the editor unchanged.
            }
        };

        // F11 in the viewport toggles its fullscreen-for-testing maximize via the dock.
        viewport.OnToggleMaximize = () => Dock?.ToggleMaximize("viewport");

        // The Graph tab is a shader-material scratch graph: the real shader node library, seeded
        // with Principled BSDF → Material Output, not bound to any node (a mesh's own material
        // graph opens from its Inspector).
        var demoRegistry = ShaderMaterialDomain.CreateRegistry();
        var demoGraph = ShaderMaterialDomain.CreateDefaultMaterialGraph("Material Scratch");
        var graphPanel = new GraphEditorPanel(
            graph: demoGraph,
            registry: demoRegistry,
            theme: _theme,
            app: _app
        );
        // A docked tab opens small and gets maximized: keep the graph in view as it resizes.
        graphPanel.AutoFrame = true;

        // ── Panel descriptors ─────────────────────────────────────────────────
        var panels = new DockPanel[] {
            new() {
                PanelId = "hierarchy",
                Title = "Hierarchy",
                Content = hierarchyPane,
            },
            new() {
                PanelId = "viewport",
                Title = "Viewport",
                Content = new ColoredBox(color: _theme.ViewportBackground, child: viewport),
            },
            new() {
                PanelId = "inspector",
                Title = "Inspector",
                Content = new Column {
                    Children = {
                        new Padding(
                            padding: EdgeInsets.Symmetric(horizontal: 6f, vertical: 4f),
                            child: new AdwSearchEntry {
                                Placeholder = "Filter properties",
                                OnChanged = f => inspector.Filter = f,
                            }
                        ),
                        new Expanded(
                            new ScrollView(
                                new Padding(padding: EdgeInsets.All(8f), child: inspector)
                            )
                        ),
                    },
                },
            },
            new() {
                // "Render", not "Settings": these are the scene's render settings — the editor's own
                // preferences live in the Settings window behind the header gear.
                PanelId = "settings",
                Title = "Render",
                Content = new Column {
                    Children = {
                        new Padding(
                            padding: EdgeInsets.Symmetric(horizontal: 6f, vertical: 4f),
                            child: new AdwSearchEntry {
                                Placeholder = "Filter render settings",
                                OnChanged = f => settings.Filter = f,
                            }
                        ),
                        new Expanded(
                            new ScrollView(
                                new Padding(padding: EdgeInsets.All(8f), child: settings)
                            )
                        ),
                    },
                },
            },
            new() {
                PanelId = "info",
                Title = "Info",
                Content = new ScrollView(new Padding(padding: EdgeInsets.All(10f), child: info)),
            },
            new() {
                // Project = file tree (top) + asset preview (bottom) in one tab, split vertically.
                PanelId = "browser",
                Title = "Project",
                Content = new AdwPaned(
                    first: new Column {
                        Children = {
                            new Padding(
                                padding: EdgeInsets.Symmetric(horizontal: 6f, vertical: 4f),
                                child: new AdwSearchEntry {
                                    Placeholder = "Search files",
                                    OnChanged = f => browser.Filter = f,
                                }
                            ),
                            new Expanded(browserScroll),
                        },
                    },
                    second: assetPreview,
                    vertical: true
                ) { Position = 0.6f },
            },
            new() {
                PanelId = "timeline",
                Title = "Timeline",
                Content = new TimelinePanel(state: State, theme: _theme),
            },
            new() {
                PanelId = "tiles",
                Title = "Tiles",
                Content = new ScrollView(
                    new Padding(
                        padding: EdgeInsets.All(6f),
                        child: new TilePalettePanel(state: State, theme: _theme, viewport: viewport)
                    )
                ),
            },
            new() {
                PanelId = "console",
                Title = "Console",
                Content = _consolePanel = new ConsolePanel(_theme) {
                    FontSize = _actions.Settings.ConsoleFontSize.Value,
                },
            },
            new() {
                PanelId = "graph",
                Title = "Graph",
                Content = graphPanel,
            },
            new() {
                PanelId = "codeeditor",
                Title = "Code",
                Content = codeEditor,
            },
        };
        _codePanel =
            panels[^1]; // the codeeditor descriptor — retitled to the open file name on open

        // Restore the saved per-project layout if present and valid; otherwise default.
        var known = panels.Select(p => p.PanelId).ToHashSet();
        var tree = (State.Preferences?.Layout.Dock.Value is { } saved
                       ? DockLayoutStore.FromData(data: saved, knownPanels: known)
                       : null) ??
                   DefaultDockTree();
        // Projects saved before a panel existed won't reference it — add it as a tab so newly
        // introduced panels (e.g. Settings, Info) are reachable without a manual layout reset.
        EnsurePanel(tree: tree, panelId: "settings", nextTo: "inspector");
        EnsurePanel(tree: tree, panelId: "info", nextTo: "settings");
        EnsurePanel(tree: tree, panelId: "graph", nextTo: "inspector");
        EnsurePanel(tree: tree, panelId: "console", nextTo: "browser");
        EnsurePanel(tree: tree, panelId: "timeline", nextTo: "console");
        EnsurePanel(tree: tree, panelId: "codeeditor", nextTo: "viewport");

        Dock = new DockLayout(
            app: _app,
            theme: _theme,
            root: tree,
            panels: panels
        );
        if (State.Preferences is { } prefs)
            Dock.LayoutChanged = () => prefs.Layout.Dock.Value = DockLayoutStore.ToData(Dock!.Root);
        return Dock;
    }

    /// Ensure
    /// <paramref name="panelId" />
    /// appears somewhere in the tree, adding it as a tab next
    /// to
    /// <paramref name="nextTo" />
    /// (or the first leaf) when a restored layout predates the panel.
    private static void EnsurePanel(DockNode tree, string panelId, string nextTo)
    {
        if (tree.LeafIds().Contains(panelId)) return;
        var leaf = FindLeafWith(node: tree, panelId: nextTo) ?? FirstLeaf(tree);
        leaf?.PanelIds.Add(panelId);
    }

    private static DockLeaf? FindLeafWith(DockNode node, string panelId)
    {
        return node switch {
            DockLeaf l => l.PanelIds.Contains(panelId) ? l : null,
            DockSplit s => FindLeafWith(node: s.First, panelId: panelId) ??
                           FindLeafWith(node: s.Second, panelId: panelId),
            _ => null,
        };
    }

    private static DockLeaf? FirstLeaf(DockNode node)
    {
        return node switch {
            DockLeaf l => l,
            DockSplit s => FirstLeaf(s.First) ?? FirstLeaf(s.Second),
            _ => null,
        };
    }

    /// <summary>
    ///     Default arrangement — a three-column IDE shell:
    ///     left = Hierarchy / Project (stacked); center = Viewport / Console+Timeline (stacked);
    ///     right = Inspector / Render+Info (stacked); Graph tabs with the viewport.
    ///     <code>
    ///     SplitH(0.18)[
    ///         SplitV(0.55)[ hierarchy | browser ]
    ///       | SplitH(0.76)[
    ///             SplitV(0.72)[ viewport | (console,timeline) ]
    ///           | SplitV(0.55)[ inspector | (settings,info,graph) ] ] ]
    ///     </code>
    /// </summary>
    private static DockNode DefaultDockTree()
    {
        return new DockSplit(
            // Left column — hierarchy over project browser.
            first: new DockSplit(
                first: new DockLeaf("hierarchy"),
                second: new DockLeaf("browser"),
                vertical: true,
                ratio: 0.55f
            ),
            // Center + right.
            second: new DockSplit(
                // Center — viewport over the console/timeline strip.
                first: new DockSplit(
                    // The graph canvas needs room: it shares the big centre leaf, not a side column.
                    first: new DockLeaf(["viewport", "codeeditor", "graph"]),
                    second: new DockLeaf(["console", "timeline"]),
                    vertical: true,
                    ratio: 0.72f
                ),
                // Right — inspector over settings/info/graph tabs.
                second: new DockSplit(
                    first: new DockLeaf("inspector"),
                    second: new DockLeaf(["settings", "info"]),
                    vertical: true,
                    ratio: 0.55f
                ),
                vertical: false,
                ratio: 0.76f
            ),
            vertical: false,
            ratio: 0.18f
        );
    }

    /// <summary>Restore the default dock arrangement (View menu + settings window).</summary>
    public void ResetLayout()
    {
        Dock?.SetRoot(DefaultDockTree());
        State.Preferences?.Layout.Dock.Reset(); // back to "no saved layout" — the default
    }

    // ── Header bar ────────────────────────────────────────────────────────────

    /// <summary>
    ///     The editor's single strip of chrome, GNOME-style: the transport and node actions packed
    ///     at the start, the scene/project title centred, the view actions plus the primary menu at
    ///     the end, and — under Adwaita CSD — the window buttons hosted by the bar itself.
    /// </summary>
    /// <param name="menus">
    ///     Menus to fold into the ☰ primary menu, or null when a native menu bar already took them.
    /// </param>
    private AdwHeaderBar BuildHeaderBar(IReadOnlyList<AppMenu>? menus)
    {
        // A header-bar icon action: flat + circular, named by its tooltip (the icon is the label).
        Widget Icon(string icon, Action onPressed, string tooltip)
        {
            return new Tooltip(
                message: tooltip,
                child: new AdwButton(label: tooltip, onPressed: onPressed) {
                    IconName = icon,
                    Style = AdwButtonStyle.Flat,
                    Circular = true,
                }
            );
        }

        // Play ⇄ Stop (the one suggested-action button) and a Pause/Resume sibling that is live
        // only while playing.
        var playBtn = new AdwButton("Play") { Style = AdwButtonStyle.Suggested };
        var pauseBtn = new AdwButton("Pause") { Enabled = false };

        // Drive both transport buttons + continuous-update from the authoritative play state, so every
        // path that changes it (these buttons, the viewport's P shortcut, a StartPlay that threw and
        // left IsPlaying false) keeps the header bar consistent. This is the SOLE owner of
        // app.ContinuousUpdate: edit mode is event-driven (the viewport change-gates its 3D render and
        // self-schedules settle frames); only a running play session renders continuously.
        void SyncTransport()
        {
            bool playing = State.IsPlaying;
            bool paused = State.IsPaused;
            bool building = State.IsScriptBuilding;
            // Paused = frozen simulation: drop continuous rendering so a paused session idles like a
            // static edit scene (the viewport keeps showing its cached frame); resume flips it back.
            _app.ContinuousUpdate = playing && !paused;
            playBtn.IconName = playing ? Icons.Stop : Icons.Play;
            playBtn.Label = playing ? "Stop" : building ? "Building…" : "Play";
            // Stopping a running game is the destructive half of the toggle, starting one the
            // suggested half — the two Adwaita accent styles, in place of a custom tone scale.
            playBtn.Style = playing ? AdwButtonStyle.Destructive : AdwButtonStyle.Suggested;
            // Can't enter play while the user scripts are still compiling (StartPlay refuses anyway, but
            // disabling the button makes that obvious). Stopping a running session is always allowed.
            playBtn.Enabled = playing || !building;
            pauseBtn.Enabled = playing; // meaningful only in play mode
            pauseBtn.IconName = paused ? Icons.Play : Icons.Pause;
            pauseBtn.Label = paused ? "Resume" : "Pause";
            pauseBtn.Style = paused ? AdwButtonStyle.Suggested : AdwButtonStyle.Regular;
            _app.RequestPaint();
        }

        _syncTransport = SyncTransport;
        playBtn.OnPressed = TogglePlay;
        pauseBtn.OnPressed = TogglePause;

        // The viewport's P shortcut toggles pause directly on the state — re-sync the buttons when it does.
        State.PlayPausedChanged += SyncTransport;
        // Keep the Play button in step with the (possibly background) script build: disabled while a
        // build is in flight, re-enabled when it finishes. Fired by EditorState.BuildScriptsAsync.
        State.ScriptBuildStatusChanged += SyncTransport;
        SyncTransport(); // initial state (Play is disabled while the project's first script build runs)

        // Add Node — a split button: pressing it drops an empty node, the arrow picks a kind.
        var nodeKinds = NodeKinds();
        var addBtn = new AdwSplitButton(label: "Add", onPressed: nodeKinds[0].Add) {
            IconName = Icons.Add,
            MenuItems = nodeKinds.Select(k => k.Label).ToArray(),
            OnMenuSelected = i => nodeKinds[i].Add(),
        };

        // Snap-to-grid dropdown — persisted per project; the session binding mirrors the
        // preference back into State.SnapGrid, which the viewport drag reads.
        string[] snapLabels = ["Grid: Off", "0.25 m", "0.5 m", "1.0 m"];
        float[] snapValues = [0f, 0.25f, 0.5f, 1.0f];
        var snapDd = new AdwDropDown(
            items: snapLabels,
            selectedIndex: Math.Max(
                val1: 0,
                val2: Array.IndexOf(array: snapValues, value: State.SnapGrid)
            ),
            onSelected: i =>
            {
                if (State.Preferences is { } p) p.Viewport.SnapGrid.Value = snapValues[i];
                else State.SnapGrid = snapValues[i];
            }
        );

        var bar = new AdwHeaderBar {
            TitleWidget = new AdwWindowTitle(
                title: Path.GetFileNameWithoutExtension(State.ScenePath) is { Length: > 0 } scene
                    ? scene
                    : "Untitled Scene",
                subtitle: State.ProjectPath is { } pp ? Path.GetFileNameWithoutExtension(pp) : null
            ),
            Start = {
                playBtn,
                pauseBtn,
                new AdwSeparator(vertical: true, margin: 8f) { Length = AdwMetrics.ButtonHeight },
                addBtn,
                Icon(
                    icon: Icons.Delete,
                    onPressed: () => State.DeleteSelected(),
                    tooltip: "Delete Node"
                ),
            },
            End = {
                Icon(icon: Icons.Undo, onPressed: () => State.History.Undo(), tooltip: "Undo"),
                Icon(icon: Icons.Redo, onPressed: () => State.History.Redo(), tooltip: "Redo"),
                Icon(icon: Icons.Save, onPressed: SaveScene, tooltip: "Save Scene"),
                Icon(icon: Icons.FolderOpen, onPressed: ReloadScene, tooltip: "Reload Scene"),
                snapDd,
                new AdwSeparator(vertical: true, margin: 8f) { Length = AdwMetrics.ButtonHeight },
                Icon(
                    icon: Icons.Search,
                    onPressed: ShowCommandPalette,
                    tooltip: "Command Palette"
                ),
                Icon(
                    icon: Icons.Settings,
                    onPressed: () => _actions.OpenSettings?.Invoke(),
                    tooltip: "Settings"
                ),
            },
        };
        // GNOME's primary menu is the last thing in the bar before the window buttons.
        if (menus is not null) bar.End.Add(new AdwMenuButton { Sections = PrimaryMenu(menus) });
        return bar;
    }

    /// <summary>Re-read the scene from disk, discarding unsaved edits.</summary>
    private void ReloadScene()
    {
        State.LoadScene(SceneGraph.Load(State.ScenePath));
        _app.ShowSnackbar($"Loaded from {State.ScenePath}");
    }

    /// <summary>
    ///     The <see cref="AppMenu" /> model flattened into the GNOME primary menu: one section per
    ///     top-level menu, split further at each separator, with a submenu ("Open Recent") folded
    ///     in under its own caption. Keeping the conversion here means the native menu bar and the
    ///     ☰ button stay one source of truth — <see cref="BuildMenus" />.
    /// </summary>
    private static List<List<AdwMenuItem>> PrimaryMenu(IReadOnlyList<AppMenu> menus)
    {
        List<List<AdwMenuItem>> sections = [];
        foreach (var menu in menus)
        {
            if (menu.Role != AppMenuRole.None) continue; // OS-populated (Window); nothing to show
            List<AdwMenuItem> section = [];
            foreach (var item in menu.Items)
            {
                if (item.Separator)
                {
                    if (section.Count > 0) sections.Add(section);
                    section = [];
                    continue;
                }

                if (item.Children is { Count: > 0 } children)
                {
                    if (section.Count > 0) sections.Add(section);
                    section = [AdwMenuItem.Header(item.Label)];
                    foreach (var child in children)
                    {
                        section.Add(
                            new AdwMenuItem(label: child.Label, onActivated: child.OnSelect) {
                                Enabled = child.IsEnabled,
                            }
                        );
                    }

                    sections.Add(section);
                    section = [];
                    continue;
                }

                section.Add(
                    new AdwMenuItem(label: item.Label, onActivated: item.OnSelect) {
                        Accel = MenuAccelerators.Display(item.Shortcut),
                        Enabled = item.IsEnabled,
                        Role =
                            item.Checked is null ? AdwMenuItemRole.Normal : AdwMenuItemRole.Check,
                        Checked = item.Checked ?? false,
                    }
                );
            }

            if (section.Count > 0) sections.Add(section);
        }

        return sections;
    }

    // ── Widget plumbing ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        _root.Measure(Constraints.Tight(width: _size.Width, height: _size.Height));
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
        _root.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: _theme.Background);
        _root.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _root.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren() => [_root];
}
