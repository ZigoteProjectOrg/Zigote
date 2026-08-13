using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Editor.Export;
using Zigote.Editor.Panels;
using Zigote.Editor.Panels.AssetPreview;
using Zigote.Editor.Scene;
using Zigote.Editor.Settings;
using Zigote.Graphs.Core;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Registry;
using Zigote.Modules.UI.CodeEditor;
using Zigote.Runtime.Scene;
// CodeEditor: Adwaita has no source view (GtkSourceView has no libadwaita counterpart either),
// so the editor keeps this one Material widget. The rest of the shell is Adwaita.
using Zigote.UI.Material;
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
    public void ApplyEditorFontPreferences(EditorSettings settings)
    {
        if (_codeEditor is { } ce)
        {
            var size = settings.EditorFontSize.Value;
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
        return new AdwToolbarView(dock) { TopBars = { header } };
    }

    // ── Menu bar ────────────────────────────────────────────────────────────────

    private IReadOnlyList<AppMenu> BuildMenus()
    {
        var recent = _actions.History.Recent.Value
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
                    () => ProjectDialogs.ShowNew(_app, _actions.OpenProject)
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
                // A running game can capture the pointer for mouselook, which hides the cursor and
                // makes every other panel unreachable. Esc in the viewport is the per-session way out
                // (this menu is not clickable while the cursor is captured); this toggle is the
                // standing veto, so a game that re-takes capture every frame can still be stopped.
                new ContextMenuItem(
                    "Allow Mouse Capture in Play",
                    ToggleMouseCapture,
                    Checked: _app.Engine.AllowRelativeMouseMode
                ),
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

    /// <summary>Forbid or re-allow capture entirely. Releases immediately when turning it off.</summary>
    private void ToggleMouseCapture()
    {
        var allowed = _app.Engine.AllowRelativeMouseMode = !_app.Engine.AllowRelativeMouseMode;
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
            _app,
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
                Content = new AdwPaned(
                    new ScrollView(new Padding(EdgeInsets.Symmetric(6f, 4f), browser)),
                    assetPreview,
                    true
                ) { Position = 0.6f },
            },
            new() {
                PanelId = "timeline",
                Title = "Timeline",
                Content = new TimelinePanel(State, _theme),
            },
            new() {
                PanelId = "tiles",
                Title = "Tiles",
                Content = new ScrollView(
                    new Padding(
                        EdgeInsets.All(6f),
                        new TilePalettePanel(State, _theme, viewport)
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
                       ? DockLayoutStore.FromData(saved, known)
                       : null) ??
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
                tooltip,
                new AdwButton(tooltip, onPressed) {
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
            var playing = State.IsPlaying;
            var paused = State.IsPaused;
            var building = State.IsScriptBuilding;
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

        playBtn.OnPressed = () =>
        {
            if (!State.IsPlaying)
            {
                if (_actions.Settings.ConsoleClearOnPlay.Value) EditorLog.Clear();
                State.StartPlay();
            }
            else
            {
                State.StopPlay();
            }

            SyncTransport();
        };
        pauseBtn.OnPressed = () =>
        {
            State.TogglePause();
            SyncTransport();
        };

        // The viewport's P shortcut toggles pause directly on the state — re-sync the buttons when it does.
        State.PlayPausedChanged += SyncTransport;
        // Keep the Play button in step with the (possibly background) script build: disabled while a
        // build is in flight, re-enabled when it finishes. Fired by EditorState.BuildScriptsAsync.
        State.ScriptBuildStatusChanged += SyncTransport;
        SyncTransport(); // initial state (Play is disabled while the project's first script build runs)

        // Add Node — a split button: pressing it drops an empty node, the arrow picks a kind.
        (string Label, Action Add)[] nodeKinds = [
            ("Empty Node", () => State.AddNode("Node", NodeKind.Empty)),
            ("Mesh Node", () => State.AddNode("Mesh", NodeKind.Mesh)),
            ("Cube", () =>
                {
                    var n = State.AddNode("Cube", NodeKind.Mesh);
                    n.MeshPath = "#cube";
                    State.NotifySceneChanged();
                }
            ),
            ("Sphere", () =>
                {
                    var n = State.AddNode("Sphere", NodeKind.Mesh);
                    n.MeshPath = "#sphere";
                    State.NotifySceneChanged();
                }
            ),
            ("Light", () => State.AddNode("Light", NodeKind.Light)),
            ("Camera", () =>
                {
                    var n = State.AddNode("Camera", NodeKind.Camera);
                    n.Position = new Vec3(0, 0, -3f);
                    State.NotifySceneChanged();
                }
            ),
            ("Script Node", () => State.AddNode("Script", NodeKind.Script)),
        ];
        var addBtn = new AdwSplitButton("Add", nodeKinds[0].Add) {
            IconName = Icons.Add,
            MenuItems = nodeKinds.Select(k => k.Label).ToArray(),
            OnMenuSelected = i => nodeKinds[i].Add(),
        };

        // Snap-to-grid dropdown — persisted per project; the session binding mirrors the
        // preference back into State.SnapGrid, which the viewport drag reads.
        string[] snapLabels = ["Grid: Off", "0.25 m", "0.5 m", "1.0 m"];
        float[] snapValues = [0f, 0.25f, 0.5f, 1.0f];
        var snapDd = new AdwDropDown(
            snapLabels,
            Math.Max(0, Array.IndexOf(snapValues, State.SnapGrid)),
            i =>
            {
                if (State.Preferences is { } p) p.Viewport.SnapGrid.Value = snapValues[i];
                else State.SnapGrid = snapValues[i];
            }
        );

        var bar = new AdwHeaderBar {
            TitleWidget = new AdwWindowTitle(
                Path.GetFileNameWithoutExtension(State.ScenePath) is { Length: > 0 } scene
                    ? scene
                    : "Untitled Scene",
                State.ProjectPath is { } pp ? Path.GetFileNameWithoutExtension(pp) : null
            ),
            Start = {
                playBtn,
                pauseBtn,
                new AdwSeparator(true, 8f) { Length = AdwMetrics.ButtonHeight },
                addBtn,
                Icon(Icons.Delete, () => State.DeleteSelected(), "Delete Node"),
            },
            End = {
                Icon(Icons.Undo, () => State.History.Undo(), "Undo"),
                Icon(Icons.Redo, () => State.History.Redo(), "Redo"),
                Icon(Icons.Save, SaveScene, "Save Scene"),
                Icon(Icons.FolderOpen, ReloadScene, "Reload Scene"),
                snapDd,
                new AdwSeparator(true, 8f) { Length = AdwMetrics.ButtonHeight },
                new FpsChip(),
                Icon(Icons.Settings, () => _actions.OpenSettings?.Invoke(), "Settings"),
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
                        section.Add(
                            new AdwMenuItem(child.Label, child.OnSelect) {
                                Enabled = child.IsEnabled,
                            }
                        );
                    sections.Add(section);
                    section = [];
                    continue;
                }

                section.Add(
                    new AdwMenuItem(item.Label, item.OnSelect) {
                        Accel = MenuAccelerators.Display(item.Shortcut),
                        Enabled = item.IsEnabled,
                        Role = item.Checked is null ? AdwMenuItemRole.Normal : AdwMenuItemRole.Check,
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
