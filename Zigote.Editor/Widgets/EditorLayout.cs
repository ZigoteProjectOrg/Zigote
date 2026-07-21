using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Editor.Export;
using Zigote.Editor.Panels;
using Zigote.Editor.Panels.AssetPreview;
using Zigote.Editor.Scene;
using Zigote.Graphs.Core;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Registry;
using Zigote.Modules.UI.CodeEditor;
using Zigote.Runtime.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Menu;

namespace Zigote.Editor.Widgets;

/// <summary>
///     Editor shell with a dockable panel layout. All panels can be dragged by their
///     header and dropped onto other panels (left / right / top / bottom) to rearrange
///     the layout. Split dividers remain draggable. Each header has a collapse button
///     (shrink the region to a strip) and a maximize button (fill the dock); the viewport
///     also maximizes with F11 for fullscreen testing.
/// </summary>
public sealed class EditorLayout : RenderWidget
{
    private const float ToolbarH = 38f;
    private const float MenuBarH = 30f;

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
        _codeEditor != null && ReferenceEquals(_app.FocusedWidget, _codeEditor);

    /// <summary>
    ///     Push the persisted editor-font preferences onto the docked code editor + console: font
    ///     sizes, and — after a "code" face swap — drop the editor's cached native text layouts
    ///     (they embed glyphs shaped with the old face).
    /// </summary>
    public void ApplyEditorFontPreferences(EditorConfig config)
    {
        if (_codeEditor is { } ce)
        {
            ce.FontSize = Math.Abs(config.EditorFontSize - 13f) < 0.01f
                ? null
                : config.EditorFontSize;
            ce.InvalidateTextLayouts();
        }

        if (_consolePanel is { } console)
        {
            console.FontSize = config.ConsoleFontSize;
            console.MarkNeedsPaint();
        }
    }

    // ── Root builder ──────────────────────────────────────────────────────────

    private Widget BuildRoot()
    {
        var toolbar = BuildToolbar();
        var dock = BuildDock();

        var column = new Column {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Start,
        };

        // Hand menus to a native bar if one is available (e.g. macOS NSMenu);
        // otherwise show the cross-platform in-window menu bar above the toolbar.
        var menus = BuildMenus();
        if (!NativeMenuBar.TryInstall(menus))
            column.Children.Add(new SizedBox(height: MenuBarH, child: new MenuBar(_app, menus)));

        column.Children.Add(new SizedBox(height: ToolbarH, child: toolbar));
        column.Children.Add(new Expanded(dock));
        return column;
    }

    // ── Menu bar ────────────────────────────────────────────────────────────────

    private IReadOnlyList<AppMenu> BuildMenus()
    {
        var recent = _actions.Config.RecentProjects
            .Select(p => new ContextMenuItem(
                    Path.GetFileNameWithoutExtension(p),
                    () => _actions.OpenProject(p)
                )
            )
            .ToList();
        if (recent.Count == 0)
            recent.Add(new ContextMenuItem("(none)", null));

        var file = new AppMenu(
            "File",
            [
                new ContextMenuItem(
                    "New Project",
                    () => ProjectDialogs.ShowNew(_app, _theme, _actions.OpenProject)
                ),
                new ContextMenuItem(
                    "Open Project…",
                    () => ProjectDialogs.ShowOpen(_app, _actions.OpenProject)
                ),
                new ContextMenuItem("Open Recent", null, Children: recent),
                new ContextMenuItem("", null, true),
                new ContextMenuItem("Save", DoSave, Shortcut: "⌘S"),
                new ContextMenuItem("Save Scene As…", SaveSceneAs),
                new ContextMenuItem("", null, true),
                new ContextMenuItem("Export Game…", () => ExportDialog.Show(_app, _theme, State)),
                new ContextMenuItem("", null, true),
                new ContextMenuItem("Close Project", _actions.CloseProject),
                new ContextMenuItem("Quit", _actions.Quit, Shortcut: "⌘Q"),
            ]
        );

        var edit = new AppMenu(
            "Edit",
            [
                new ContextMenuItem("Undo", DoUndo, Shortcut: "⌘Z"),
                new ContextMenuItem("Redo", DoRedo, Shortcut: "⌘⇧Z"),
                new ContextMenuItem("", null, true),
                new ContextMenuItem("Reset Layout", ResetLayout),
            ]
        );

        var nativeBar = NativeMenuBar.Backend is not null && NativeMenuBar.Enabled;

        // Native bar only: a Window menu with the AppKit role — the OS appends Minimize/Zoom and
        // the live window list. The in-window bar has nothing useful to render there.
        if (nativeBar)
        {
            var window = new AppMenu("Window", [], AppMenuRole.Window);
            return [file, edit, window];
        }

        // On macOS "About Zigote Editor" lives in the native app menu (NativeMenuBar.AboutRequested);
        // the in-window bar needs its own Help menu to reach the about screen.
        var help = new AppMenu(
            "Help",
            [
                new ContextMenuItem(
                    "About Zigote Editor",
                    () => NativeMenuBar.AboutRequested?.Invoke()
                ),
            ]
        );
        return [file, edit, help];
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
            _app,
            _theme,
            State.ScenePath,
            p =>
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
            File.WriteAllText(_openFilePath, _codeEditor.Text);
            Dock?.SetPanelDirty("codeeditor", false);
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
        var hierarchy = new HierarchyPanel(State, _theme);
        // Wrap the tree in a scroller and let the panel reveal the selected row into view (the +4 matches
        // the Padding below). A selection made in the viewport now scrolls the hierarchy to it.
        var hierarchyScroll = new ScrollView(new Padding(EdgeInsets.All(4f), hierarchy));
        hierarchy.OnRevealRequested = (top, h) => hierarchyScroll.EnsureVisible(top + 4f, h);
        var viewport = new ViewportPanel(State, _theme);
        var inspector = new InspectorPanel(State, _theme, _app);
        var browser = new AssetBrowserPanel(State, _theme);
        var settings = new SettingsPanel(State, _theme);
        var info = new InfoPanel(_theme);
        var assetPreview = new AssetPreviewPanel(State, _theme);

        // Code editor: opened from the asset browser via OpenFileRequested (double-click a text/code file).
        // Editable, with ⌘S save + ⌘Z/⌘⇧Z undo/redo; edits mark the tab dirty until saved.
        var codeEditor = new CodeEditor();
        _codeEditor = codeEditor;
        // Any edit marks the tab unsaved; ⌘S (when the editor is focused) writes it back to disk.
        codeEditor.OnChanged += _ =>
        {
            if (_openFilePath != null) Dock?.SetPanelDirty("codeeditor", true);
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
                Dock?.SetPanelDirty("codeeditor", false);
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

        // Demo graph shown in the Graph panel — domain-agnostic empty document.
        var demoRegistry = new GraphDomainRegistry();
        var demoGraph = new GraphDocument {
            Id = Guid.NewGuid(),
            Name = "New Graph",
            DomainId = "zigote.shader",
            SchemaId = "shader.material",
        };
        var graphPanel = new GraphEditorPanel(
            demoGraph,
            demoRegistry,
            _theme,
            _app
        );

        // ── Panel descriptors ─────────────────────────────────────────────────
        var panels = new DockPanel[] {
            new() {
                PanelId = "hierarchy",
                Title = "Hierarchy",
                Content = hierarchyScroll,
            },
            new() {
                PanelId = "viewport",
                Title = "Viewport",
                Content = new ColoredBox(_theme.ViewportBackground, viewport),
            },
            new() {
                PanelId = "inspector",
                Title = "Inspector",
                Content = new ScrollView(new Padding(EdgeInsets.All(8f), inspector)),
            },
            new() {
                PanelId = "settings",
                Title = "Settings",
                Content = new ScrollView(new Padding(EdgeInsets.All(8f), settings)),
            },
            new() {
                PanelId = "info",
                Title = "Info",
                Content = new ScrollView(new Padding(EdgeInsets.All(10f), info)),
            },
            new() {
                // Project = file tree (top) + asset preview (bottom) in one tab, split vertically.
                PanelId = "browser",
                Title = "Project",
                Content = new SplitPane(
                    _theme,
                    new ScrollView(new Padding(EdgeInsets.Symmetric(6f, 4f), browser)),
                    assetPreview
                ) {
                    Vertical = true,
                    SplitRatio = 0.6f,
                },
            },
            new() {
                PanelId = "timeline",
                Title = "Timeline",
                Content = new TimelinePanel(State, _theme),
            },
            new() {
                PanelId = "console",
                Title = "Console",
                Content = _consolePanel = new ConsolePanel(_theme) {
                    FontSize = _actions.Config.ConsoleFontSize,
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
        var tree = (State.ProjectPath is { } pp ? DockLayoutStore.Load(pp, known) : null) ??
                   DefaultDockTree();
        // Projects saved before a panel existed won't reference it — add it as a tab so newly
        // introduced panels (e.g. Settings, Info) are reachable without a manual layout reset.
        EnsurePanel(tree, "settings", "inspector");
        EnsurePanel(tree, "info", "settings");
        EnsurePanel(tree, "graph", "inspector");
        EnsurePanel(tree, "console", "browser");
        EnsurePanel(tree, "timeline", "console");
        EnsurePanel(tree, "codeeditor", "viewport");

        Dock = new DockLayout(
            _app,
            _theme,
            tree,
            panels
        );
        if (State.ProjectPath is { } projPath)
            Dock.LayoutChanged = () => DockLayoutStore.Save(projPath, Dock!.Root);
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
        var leaf = FindLeafWith(tree, nextTo) ?? FirstLeaf(tree);
        leaf?.PanelIds.Add(panelId);
    }

    private static DockLeaf? FindLeafWith(DockNode node, string panelId)
    {
        return node switch {
            DockLeaf l => l.PanelIds.Contains(panelId) ? l : null,
            DockSplit s => FindLeafWith(s.First, panelId) ?? FindLeafWith(s.Second, panelId),
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
    ///     right = Inspector / Settings+Info+Graph (stacked).
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
            new DockSplit(
                new DockLeaf("hierarchy"),
                new DockLeaf("browser"),
                true,
                0.55f
            ),
            // Center + right.
            new DockSplit(
                // Center — viewport over the console/timeline strip.
                new DockSplit(
                    new DockLeaf(["viewport", "codeeditor"]),
                    new DockLeaf(["console", "timeline"]),
                    true,
                    0.72f
                ),
                // Right — inspector over settings/info/graph tabs.
                new DockSplit(
                    new DockLeaf("inspector"),
                    new DockLeaf(["settings", "info", "graph"]),
                    true,
                    0.55f
                ),
                false,
                0.76f
            ),
            false,
            0.18f
        );
    }

    /// <summary>Restore the default dock arrangement (View menu + settings window).</summary>
    public void ResetLayout()
    {
        Dock?.SetRoot(DefaultDockTree());
        if (State.ProjectPath is { } pp && Dock != null) DockLayoutStore.Save(pp, Dock.Root);
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private Widget BuildToolbar()
    {
        ToolbarButton Tb(string? icon, Action? onClick, string? label = null,
            ToolbarTone tone = ToolbarTone.Default, bool dropdown = false)
        {
            return new ToolbarButton(icon, onClick, label) {
                Tone = tone,
                Dropdown = dropdown,
            };
        }

        // Thin hairline between action groups.
        Widget Sep()
        {
            return new Padding(
                EdgeInsets.Symmetric(6f, 8f),
                new SizedBox(1f, 20f, new ColoredBox(_theme.Separator))
            );
        }

        // 2-pt gap between sibling buttons inside one group.
        Widget Gap()
        {
            return new SizedBox(2f);
        }

        // Project chip (left) — current scene/project name with a selector affordance.
        var projectName = State.ProjectPath is { } pp
            ? Path.GetFileNameWithoutExtension(pp)
            : "Untitled";
        var projectChip = Tb(
            Icons.Dashboard,
            null,
            projectName,
            dropdown: true
        );

        // Play ⇄ Stop toggle (the one prominent, accent-filled action) + a Pause/Resume sibling that is
        // only live while playing.
        ToolbarButton? playBtn = null;
        ToolbarButton? pauseBtn = null;

        // Drive both transport buttons + continuous-update from the authoritative play state, so every
        // path that changes it (these buttons, the viewport's P shortcut, a StartPlay that threw and
        // left IsPlaying false) keeps the toolbar consistent. This is the SOLE owner of
        // app.ContinuousUpdate: edit mode is event-driven (the viewport change-gates its 3D render and
        // self-schedules settle frames); only a running play session renders continuously.
        void SyncTransport()
        {
            var playing = State.IsPlaying;
            var paused = State.IsPaused;
            var building = State.IsScriptBuilding;
            // Paused = frozen simulation: drop continuous rendering so a paused session idles like a
            // static edit scene (the viewport keeps showing its cached frame); resume flips it back.
            _app.ContinuousUpdate = playing && !paused;
            playBtn!.Icon = playing ? Icons.Stop : Icons.Play;
            playBtn.Label = playing ? "Stop" : building ? "Building…" : "Play";
            playBtn.Tone = playing ? ToolbarTone.Danger : ToolbarTone.Primary;
            // Can't enter play while the user scripts are still compiling (StartPlay refuses anyway, but
            // disabling the button makes that obvious). Stopping a running session is always allowed.
            playBtn.Enabled = playing || !building;
            pauseBtn!.Enabled = playing; // meaningful only in play mode
            pauseBtn.Icon = paused ? Icons.Play : Icons.Pause;
            pauseBtn.Label = paused ? "Resume" : "Pause";
            pauseBtn.Tone = paused ? ToolbarTone.Primary : ToolbarTone.Default;
            _app.RequestPaint();
        }

        playBtn = Tb(
            Icons.Play,
            () =>
            {
                if (!State.IsPlaying)
                {
                    if (_actions.Config.ConsoleClearOnPlay) EditorLog.Clear();
                    State.StartPlay();
                }
                else
                {
                    State.StopPlay();
                }

                SyncTransport();
            },
            "Play",
            ToolbarTone.Primary
        );

        pauseBtn = Tb(
            Icons.Pause,
            () =>
            {
                State.TogglePause();
                SyncTransport();
            },
            "Pause"
        );
        pauseBtn.Enabled = false;

        // The viewport's P shortcut toggles pause directly on the state — re-sync the buttons when it does.
        State.PlayPausedChanged += SyncTransport;
        // Keep the Play button in step with the (possibly background) script build: disabled while a
        // build is in flight, re-enabled when it finishes. Fired by EditorState.BuildScriptsAsync.
        State.ScriptBuildStatusChanged += SyncTransport;
        SyncTransport(); // initial state (Play is disabled while the project's first script build runs)

        // Add Node menu.
        ToolbarButton? addBtn = null;
        var addMenu = new ContextMenu(
            new ContextMenuItem("Empty Node", () => State.AddNode("Node", NodeKind.Empty)),
            new ContextMenuItem("Mesh Node", () => State.AddNode("Mesh", NodeKind.Mesh)),
            new ContextMenuItem(
                "Cube",
                () =>
                {
                    var n = State.AddNode("Cube", NodeKind.Mesh);
                    n.MeshPath = "#cube";
                    State.NotifySceneChanged();
                }
            ),
            new ContextMenuItem(
                "Sphere",
                () =>
                {
                    var n = State.AddNode("Sphere", NodeKind.Mesh);
                    n.MeshPath = "#sphere";
                    State.NotifySceneChanged();
                }
            ),
            new ContextMenuItem("Light", () => State.AddNode("Light", NodeKind.Light)),
            new ContextMenuItem(
                "Camera",
                () =>
                {
                    var n = State.AddNode("Camera", NodeKind.Camera);
                    n.Position = new Vec3(0, 0, -3f);
                    State.NotifySceneChanged();
                }
            ),
            new ContextMenuItem("Script Node", () => State.AddNode("Script", NodeKind.Script))
        );
        addBtn = Tb(
            Icons.Add,
            () => addMenu.ShowAt(new Offset(addBtn!.Bounds.X, addBtn.Bounds.Bottom + 4f)),
            "Add",
            dropdown: true
        );

        var deleteBtn = Tb(Icons.Delete, () => State.DeleteSelected());
        var undoBtn = Tb(Icons.Undo, () => State.History.Undo());
        var redoBtn = Tb(Icons.Redo, () => State.History.Redo());
        var saveBtn = Tb(
            Icons.Save,
            () =>
            {
                State.Scene.Save(State.ScenePath);
                State.SaveAssets();
                State.SaveProjectSettings(); // persist render settings (minus debug) into the .zigoteproj
                _app.ShowSnackbar($"Saved to {State.ScenePath}");
            },
            "Save"
        );
        var loadBtn = Tb(
            Icons.FolderOpen,
            () =>
            {
                var loaded = SceneGraph.Load(State.ScenePath);
                State.LoadScene(loaded);
                _app.ShowSnackbar($"Loaded from {State.ScenePath}");
            },
            "Load"
        );

        // Settings window (gear, right-aligned next to the FPS chip).
        var settingsBtn = new Tooltip(
            "Settings",
            Tb(Icons.Settings, () => _actions.OpenSettings?.Invoke())
        );

        // Snap-to-grid dropdown.
        string[] snapLabels = ["Grid: Off", "0.25 m", "0.5 m", "1.0 m"];
        float[] snapValues = [0f, 0.25f, 0.5f, 1.0f];
        var snapDd = new Dropdown<string>(
            snapLabels,
            0,
            s => s,
            (i, _) => State.SnapGrid = snapValues[i]
        );

        // The toolbar is its own elevation layer (a touch lighter than the window) closed off by a
        // hairline along its bottom edge — grouped icon actions, the layered look from the spec.
        var row = new Padding(
            EdgeInsets.Symmetric(8f, 0f),
            new Row {
                MainAxisAlignment = MainAxisAlignment.Start,
                CrossAxisAlignment = CrossAxisAlignment.Center,
                Children = {
                    projectChip,
                    Sep(),
                    playBtn,
                    Gap(),
                    pauseBtn,
                    Sep(),
                    addBtn,
                    Gap(),
                    deleteBtn,
                    Sep(),
                    undoBtn,
                    Gap(),
                    redoBtn,
                    Sep(),
                    saveBtn,
                    Gap(),
                    loadBtn,
                    Sep(),
                    new SizedBox(104f, 26f, snapDd),
                    new Spacer(),
                    settingsBtn,
                    Gap(),
                    new FpsChip(),
                    new SizedBox(8f),
                },
            }
        );

        return new ColoredBox(
            _theme.Toolbar,
            new Column {
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
                Children = {
                    new Expanded(row),
                    new SizedBox(height: 1f, child: new ColoredBox(_theme.Border)),
                },
            }
        );
    }

    // ── Widget plumbing ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        _root.Measure(Constraints.Tight(_size.Width, _size.Height));
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
        _root.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, _theme.Background);
        _root.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _root.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_root];
    }
}