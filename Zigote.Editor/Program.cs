using System.Diagnostics;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Rendering;
using Zigote.Core.State;
using Zigote.Editor;
using Zigote.Editor.Export;
using Zigote.Editor.Scene;
using Zigote.Editor.Settings;
using Zigote.Editor.Vfx;
using Zigote.Editor.Widgets;
using Zigote.Persistence.SQLite;
using Zigote.Preferences;
using Zigote.Runtime.Scene;
using Zigote.Runtime.Vfx;
using Zigote.UI.DevTools;
using Zigote.UI.Host;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Menu;

// Headless game export (no window, no engine init) — used by CI and scripting.
string[] cli = Environment.GetCommandLineArgs();
if (cli.Length > 1 && cli[1] == "--export")
    return await ExportCli.RunAsync(cli);

const string appName = "Zigote Editor";

// Editor settings and project history: reactive Preference<T>s (Zigote.Preferences) over the
// SQLite store at preferences.db — two provider groups on one store, so the Settings window's
// "Reset All" (the "editor" group) leaves the recent-project list (the "projects" group) alone.
using var preferenceStore = new PreferenceStore(new SqliteKeyValueStore(EditorSettings.DbPath));
var settings = new EditorSettings(preferenceStore);
var history = new ProjectHistory(preferenceStore);

// Tee stdout into the editor Console panel before the engine starts logging.
EditorLog.CaptureConsole();

// The runtime resolves VFX emitter assets through this seam: the editor compiles node graphs live,
// while an exported player reads the baked JSON instead (same pattern as Physics.Backend).
VfxAssets.GraphCompiler = n => VfxNodeEditor.Compile(n).Asset;

// Adwaita is the editor's design system: read GNOME's accent, color-scheme and titlebar button
// layout (and start monitoring them) before anything reads a theme, and settle the window chrome
// preference now — rounded CSD corners need an alpha-composited window, which can only be asked
// for at creation time.
GnomeDesktop.Start();
WindowChrome.Preference = EditorPreferences.ParseChrome(settings.WindowChromeMode.Value);

// The editor drives a 3D viewport, so it takes the fastest GPU on a multi-GPU machine (a plain UI
// App defaults to the power-efficient one). settings.GpuIndex pins a specific one for testing —
// read here because the GPU is chosen when the device is created and never afterwards.
using var app = new App(
    title: appName,
    width: 1280,
    height: 800,
    gpuPreference: GpuPowerPreference.Performance,
    gpuIndex: settings.GpuIndex.Value,
    transparentWindow: WindowChrome.Resolve() == WindowChromeStyle.AdwaitaCsd
);

// Editor preferences (theme mode, fonts, vsync) — reactive appliers over the EditorSettings
// preferences; the Settings window only writes preference values. Theme mode "system" follows
// the OS appearance (SystemThemeChanged below).
var prefs = new EditorPreferences(app: app, settings: settings, history: history);
var theme = prefs.ResolveTheme();
app.Theme = theme;
prefs.ApplyAtBoot();
DevTools.Install(app: app, profile: DevToolsProfile.ThreeD);

// Dev tooling: when ZIGOTE_SHOT is set the native engine dumps a one-shot framebuffer
// capture at frame ZIGOTE_SHOT_FRAME. Force continuous rendering so the frame counter
// reliably advances to that frame even on an otherwise-static scene. ForceContinuousRender
// (not ContinuousUpdate, which the toolbar transport owns — it would clobber this) also
// bypasses the viewport's change-gated 3D render, so every frame is a real render.
bool shotMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZIGOTE_SHOT"));
if (shotMode)
    app.ForceContinuousRender = true;

// On macOS, route menus to the native NSMenu bar; elsewhere EditorLayout falls back
// to the in-window menu bar automatically (NativeMenuBar.Backend stays null).
if (MacMenu.IsSupported)
    NativeMenuBar.Backend = new MacNativeMenuBar(appName);

// The app menu's "About Zigote Editor" (and the in-window Help menu elsewhere) opens the
// editor about screen: version + the LicenseRegistry attributions.
NativeMenuBar.AboutRequested = () => AboutDialog.Show(app);

// The currently open project session, or null while the welcome screen is showing.
EditorState? session = null;
EditorLayout? layoutWidget = null;
// Session-scoped preference→state bindings (viewport toggles, reduced graphics); the preferences
// are the write path, these mirror them into the fast EditorState flags. Disposed with the session.
List<IDisposable> sessionBindings = [];

// The Settings OS window (toolbar gear / app menu). One instance app-wide; reopens on demand.
var settingsHost = new SettingsWindowHost(prefs) { LayoutProvider = () => layoutWidget };

// Cross-window panel docking: tear dock tabs out into floating OS windows, drag them between
// windows, return them to the main dock on ✕. Re-bound to each new shell in Open/RebuildShell.
var dockWindows = new DockWindowManager(app);

// Declared-then-assigned so the local functions below (which the initializer references) can
// capture it without tripping definite-assignment.
EditorActions actions = null!;
actions = new EditorActions {
    OpenProject = OpenProject,
    CloseProject = ShowWelcome,
    Quit = app.RequestQuit,
    History = history,
    Settings = settings,
    OpenSettings = settingsHost.Open,
};

void CloseSession()
{
    dockWindows.CloseAllForRebuild(); // floating panels belong to the closing shell
    foreach (var binding in sessionBindings) binding.Dispose();
    sessionBindings.Clear();
    session?.Dispose();
    session = null;
    layoutWidget = null;
}

// Theme mode / UI-font-scale changes rebuild the shell: editor panels take their ThemeData by
// constructor, so restyling means a fresh EditorLayout (same live session) or welcome screen.
void RebuildShell()
{
    theme = prefs.ResolveTheme();
    app.Theme = theme;
    if (session is { } s)
    {
        // Floating panels hold old-theme panel instances; the new shell builds every panel
        // fresh, so close the floats and re-open their panels in the new main dock.
        var floating = dockWindows.CloseAllForRebuild();
        layoutWidget = new EditorLayout(
            state: s,
            theme: theme,
            app: app,
            actions: actions
        );
        app.Root = layoutWidget;
        layoutWidget.ApplyEditorFontPreferences(settings);
        if (layoutWidget.Dock is { } dock)
        {
            dockWindows.SetMain(dock: dock, theme: theme);
            foreach (string id in floating) dock.OpenPanel(id);
        }
    }
    else
        ShowWelcome();

    settingsHost.ApplyTheme();
    app.RequestPaint();
}

prefs.ThemeChanged += RebuildShell;
prefs.EditorFontChanged += () => layoutWidget?.ApplyEditorFontPreferences(settings);
app.SystemThemeChanged += _ => prefs.OnSystemThemeChanged();

void ShowWelcome()
{
    CloseSession();
    // The welcome screen is a static 2D surface — let the idle event-wait gate throttle it.
    app.ContinuousUpdate = false;
    app.Root = new WelcomeScreen(
        app: app,
        theme: theme,
        history: history,
        onOpen: OpenProject
    );
    // Replace any project menu with a minimal welcome menu (native bar only; the
    // in-window welcome screen already has on-screen New/Open buttons).
    NativeMenuBar.TryInstall(
        [
            new AppMenu(
                Title: "File",
                Items: [
                    new ContextMenuItem(
                        Label: "New Project",
                        OnSelect: () => ProjectDialogs.ShowNew(app: app, onOpen: OpenProject)
                    ),
                    new ContextMenuItem(
                        Label: "Open Project…",
                        OnSelect: () => ProjectDialogs.ShowOpen(app: app, onOpen: OpenProject)
                    ),
                    new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                    new ContextMenuItem(Label: "Settings…", OnSelect: settingsHost.Open),
                    new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                    new ContextMenuItem(Label: "Quit", OnSelect: app.RequestQuit, Shortcut: "⌘Q"),
                ]
            ),
        ]
    );
}

void OpenProject(string projectFile)
{
    projectFile = Path.GetFullPath(projectFile);
    if (!File.Exists(projectFile))
    {
        Console.Error.WriteLine($"[Zigote] Project not found: {projectFile}");
        history.Forget(projectFile);
        ShowWelcome();
        return;
    }

    CloseSession();

    var project = ZigoteProject.Load(projectFile);
    string projDir = Path.GetDirectoryName(projectFile) ?? ".";

    // Resolve all project-relative paths (scenes, meshes, textures) from the project root,
    // regardless of where the editor process was launched.
    Directory.SetCurrentDirectory(projDir);

    var state = new EditorState {
        ProjectPath = projectFile,
        ProjectDir = projDir,
        AssetRoot = project.AssetRoot,
        ScenePath = project.StartupScene,
        Project = project,
    };
    state.SceneChanged += app.RequestPaint;
    state.SelectionSignal.Changed += _ => app.RequestPaint();
    state.AssetsChanged += app.RequestPaint;

    // Per-project preferences: viewport toggles + dock layout, in the project-relative
    // <project>.prefs.json. The debug-console variables and toolbar write the preferences; the
    // bindings below mirror each one into the fast EditorState flag the viewport reads per paint
    // and apply its side effects. Both sides are equality-gated, so the pairs can't loop.
    var projectPrefs = new ProjectPreferences(projectFile);
    state.Preferences = projectPrefs;
    var viewport = projectPrefs.Viewport;
    state.ShowPhysicsWireframe = viewport.PhysicsWireframe.Value;
    state.StreamDistance = MathF.Max(x: 0f, y: viewport.StreamDistance.Value);
    state.UseNativeVfx = viewport.NativeVfx.Value;
    state.UseGpuVfx = viewport.GpuVfx.Value;
    state.AnimateEditVfx = viewport.AnimateEditVfx.Value;
    state.SnapGrid = viewport.SnapGrid.Value;
    sessionBindings.Add(
        viewport.PhysicsWireframe.Observe(() =>
            {
                state.ShowPhysicsWireframe = viewport.PhysicsWireframe.Peek();
                app.RequestPaint();
            }
        )
    );
    sessionBindings.Add(
        viewport.StreamDistance.Observe(() =>
            {
                state.StreamDistance = MathF.Max(x: 0f, y: viewport.StreamDistance.Peek());
                if (!state.StreamingEnabled) state.MeshStreamer.Clear();
                state.InvalidateViewport();
                app.RequestPaint();
            }
        )
    );
    sessionBindings.Add(
        viewport.NativeVfx.Observe(() =>
            {
                state.UseNativeVfx = viewport.NativeVfx.Peek();
                ZigoteEngine.Instance?.ParticlesClearAll();
                state.InvalidateViewport();
                app.RequestPaint();
            }
        )
    );
    sessionBindings.Add(
        viewport.GpuVfx.Observe(() =>
            {
                state.UseGpuVfx = viewport.GpuVfx.Peek();
                ZigoteEngine.Instance?.ParticlesClearAll();
                state.InvalidateViewport();
                app.RequestPaint();
            }
        )
    );
    sessionBindings.Add(
        viewport.AnimateEditVfx.Observe(() =>
            {
                state.AnimateEditVfx = viewport.AnimateEditVfx.Peek();
                state.InvalidateViewport();
                app.RequestPaint();
            }
        )
    );
    sessionBindings.Add(viewport.SnapGrid.Observe(() => state.SnapGrid = viewport.SnapGrid.Peek()));

    // Physics-wireframe overlay toggle (debug menu Variables tab / `set render.physics_wireframe 1`).
    // Editor-side debug viz; draws collision shapes over the viewport in edit + play mode.
    DebugVariables.RegisterBool(
        name: "render.physics_wireframe",
        getter: () => viewport.PhysicsWireframe.Peek(),
        setter: v => viewport.PhysicsWireframe.Value = v,
        category: "3D · Render",
        description: "Draw physics collision shapes as a wireframe overlay"
    );

    // Demand mesh streaming: load .zmesh meshes within this camera distance off-thread and unload
    // (hide) them beyond it. 0 = off (all meshes stay resident — the default synchronous path). The
    // per-frame asset pump (session.PumpAssets) is always live; only the residency sink gates on this.
    DebugVariables.RegisterFloat(
        name: "render.stream_distance",
        getter: () => viewport.StreamDistance.Peek(),
        setter: v => viewport.StreamDistance.Value = MathF.Max(x: 0f, y: v),
        min: 0f,
        max: 1000f,
        category: "3D · Render",
        description:
        "Demand-stream .zmesh meshes within this camera distance (0 = off, all resident)"
    );

    // VFX particles through the native GPU billboard pass vs. the editor 2D-projection overlay. Off by
    // default (the 2D path is always available); clearing it drops any native batches still uploaded.
    DebugVariables.RegisterBool(
        name: "render.vfx_native",
        getter: () => viewport.NativeVfx.Peek(),
        setter: v => viewport.NativeVfx.Value = v,
        category: "3D · Render",
        description:
        "Render VFX particles with the native GPU billboard pass (additive + alpha blend)"
    );

    // Simulate VFX emitters on the GPU (compute kernel) — the scale path. Clearing it drops the GPU batches.
    DebugVariables.RegisterBool(
        name: "render.vfx_gpu",
        getter: () => viewport.GpuVfx.Peek(),
        setter: v => viewport.GpuVfx.Value = v,
        category: "3D · Render",
        description: "Simulate VFX particles on the GPU (compute) instead of the CPU"
    );

    // Continuously animate VFX emitters in edit mode. Off by default — a static first-frame preview is
    // shown instead (no continuous render). On = the editor renders continuously to animate them.
    DebugVariables.RegisterBool(
        name: "render.vfx_edit",
        getter: () => viewport.AnimateEditVfx.Peek(),
        setter: v => viewport.AnimateEditVfx.Value = v,
        category: "3D · Render",
        description: "Animate VFX emitters live in edit mode (off = static preview)"
    );

    // Native frustum-culling toggle (debug menu Variables / `set render.frustum_cull 0`). On by default;
    // the renderer skips draws whose bounding sphere is outside the camera frustum. Toggling off draws
    // everything — watch the Renderer panel's draw/tri counts drop when it's on and you look away.
    bool frustumCull = true;
    DebugVariables.RegisterBool(
        name: "render.frustum_cull",
        getter: () => frustumCull,
        setter: v =>
        {
            frustumCull = v;
            ZigoteEngine.Instance?.RenderSetFrustumCull(v);
            state.InvalidateViewport();
            app.RequestPaint();
        },
        category: "3D · Render",
        description: "Frustum-cull meshes whose bounds fall outside the camera view"
    );

    // ZIGOTE_SCENE=balls launches the HDRI material-showcase scene (glass/chrome/paint/gold/etc.).
    var scene = Environment.GetEnvironmentVariable("ZIGOTE_SCENE")?.ToLowerInvariant() switch {
        "balls" or "materialballs" => SceneGraph.MaterialBalls(),
        _ => File.Exists(project.StartupScene)
            ? SceneGraph.Load(project.StartupScene)
            : SceneGraph.Demo(),
    };
    state.LoadScene(scene);
    state.LoadAssets();

    // Apply the project's saved render settings (environment/post/shadows/material) to the engine
    // before the Settings panel reads them. A project with none keeps the engine defaults (DoF off).
    state.ApplyProjectRenderSettings();

    // Edit-mode reduced graphics (TAA/bloom/SSR/DoF off while authoring; play always runs full).
    // An editor preference, not a project setting — bound both ways to the reactive preference
    // (both sides are equality-gated, so the pair can't loop), also reachable from the debug
    // console as `render.editor_reduced`. ZIGOTE_SHOT captures ignore it so golden images never
    // depend on a per-machine preference.
    state.ReducedEditorGraphics = !shotMode && settings.ReducedEditorGraphics.Value;
    if (!shotMode)
    {
        sessionBindings.Add(
            settings.ReducedEditorGraphics.Observe(() =>
                state.ReducedEditorGraphics = settings.ReducedEditorGraphics.Peek()
            )
        );
    }

    state.EditorGraphicsChanged += () =>
    {
        settings.ReducedEditorGraphics.Value = state.ReducedEditorGraphics;
        app.RequestPaint();
    };
    DebugVariables.RegisterBool(
        name: "render.editor_reduced",
        getter: () => state.ReducedEditorGraphics,
        setter: v => state.ReducedEditorGraphics = v,
        category: "3D · Render",
        description:
        "Reduced edit-mode viewport graphics (no TAA/bloom/SSR/DoF); play mode always renders full"
    );

    // Auto-build and hot-watch the project's script assembly if one is configured.
    if (!string.IsNullOrEmpty(project.ScriptProject))
    {
        string scriptProjPath = Path.Combine(path1: projDir, path2: project.ScriptProject);
        if (File.Exists(scriptProjPath))
        {
            state.StartScriptWatcher(scriptProjPath);
            _ = state.BuildScriptsAsync(scriptProjPath);
        }
        else
            Console.WriteLine($"[Zigote] ScriptProject not found: {scriptProjPath}");
    }

    session = state;
    layoutWidget = new EditorLayout(
        state: state,
        theme: theme,
        app: app,
        actions: actions
    );
    app.Root = layoutWidget;
    layoutWidget.ApplyEditorFontPreferences(settings);
    if (layoutWidget.Dock is { } mainDock) dockWindows.SetMain(dock: mainDock, theme: theme);

    // Edit mode is event-driven: the App idles in WaitEvents until something requests a paint
    // (SceneChanged/SelectionSignal/AssetsChanged above, input, tickers), and the viewport itself
    // change-gates the native 3D render + renders trailing settle frames so TAA/SSGI converge after
    // each change (see ViewportPanel.ShouldRender3D). ContinuousUpdate is owned by the toolbar
    // transport (EditorLayout.SyncTransport: true only while playing) — do not force it here.

    history.RecordOpened(projectFile);
}

// ── Decide the initial screen ───────────────────────────────────────────────
// Explicit CLI arg wins; otherwise reopen the last project (if the preference allows);
// otherwise welcome.
string[] argsArray = Environment.GetCommandLineArgs();
if (argsArray.Length > 1 && !string.IsNullOrWhiteSpace(argsArray[1]))
    OpenProject(argsArray[1]);
else if (settings.ReopenLastProject.Value && history.Last.Value is { Length: > 0 } lastProject &&
         File.Exists(lastProject))
    OpenProject(lastProject);
else
    ShowWelcome();

// Dev hook: open the Settings window at boot (multi-window smoke testing).
if (Environment.GetEnvironmentVariable("ZIGOTE_OPEN_SETTINGS") == "1")
    settingsHost.Open();

// Dev hook: enter play mode as soon as the project's scripts finish building — pairs with
// ZIGOTE_SHOT for hands-free gameplay captures.
bool autoPlay = Environment.GetEnvironmentVariable("ZIGOTE_AUTOPLAY") == "1";

// ── Main loop ────────────────────────────────────────────────────────────────
// Editor renders up to 120 fps for a smoother UI (on 120 Hz / ProMotion displays; vsync still caps
// to the panel's refresh rate). Play-mode game timing is managed by the GameSession itself
// (it advances physics/scripts from dt), so it is independent of this editor render cap.
// A backgrounded editor (window focus lost, not playing) drops to a coarse heartbeat — the common
// edit-in-external-IDE workflow shouldn't burn CPU/GPU on frames nobody sees. Play mode keeps full
// rate in the background so a running game stays live.
// Focused rate comes from app.FrameIntervalTicks (the monitor's refresh, or the user's FPS cap when
// that is slower) and is re-read each frame — dragging the editor from a 60 Hz to a 144 Hz screen
// re-paces the loop without a restart.
const int backgroundFps = 10;
long backgroundTicks = Stopwatch.Frequency / backgroundFps;
var clock = Stopwatch.StartNew();
long assetFrame = 0L;

while (!app.ShouldQuit)
{
    long frameStart = clock.ElapsedTicks;
    // GNOME publishes accent / color-scheme / titlebar-button changes on a background monitor
    // process; the flag it sets is drained here because the accent restyles the shell in every
    // theme mode, not just "system" (which SystemThemeChanged already covers).
    prefs.PumpSystemStyle();

    Profiler.BeginFrame();
    using (Profiler.Scope("Frame"))
    {
        using (Profiler.Scope("App.Frame")) app.Frame();

        using (Profiler.Scope("Update.Play"))
        {
            if (autoPlay && session is { IsPlaying: false, IsScriptBuilding: false } playable)
            {
                playable.StartPlay();
                autoPlay = !playable.IsPlaying; // keep retrying only while it refuses
            }

            session?.TickPlay(app.DeltaTime);
        }

        using (Profiler.Scope("Update.Animation")) session?.TickAnimation(app.DeltaTime);

        using (Profiler.Scope("Assets.Pump"))
        {
            if (session?.PumpAssets(assetFrame++) == true)
                app.RequestPaint();
        }

        using (Profiler.Scope("Background"))
            // Whatever the frame can afford of the deferred work: results that asked to wait for
            // room (Deliver.WhenIdle) and a slice of anything filling across frames. A quarter of a
            // 60 Hz frame, so a burst of results costs several frames of settling rather than one
            // visible stall. Whatever does not fit asks for another frame and continues there.
            session?.Background.RunFrame(TimeSpan.FromMilliseconds(4));
    }

    Profiler.EndFrame();

    // Decide the pace AFTER the frame, so the focus event app.Frame just processed takes effect
    // before we sleep — refocus latency is one 8 ms pad, not a stale ~100 ms background sleep.
    // AnyWindowFocused (not WindowFocused): a focused Settings window must keep full rate — its
    // frames are pumped from this same loop, and typing at the background heartbeat feels broken.
    bool throttled = !app.AnyWindowFocused && session is not { IsPlaying: true } &&
                     !app.ForceContinuousRender;
    long targetTicks = throttled ? backgroundTicks : app.FrameIntervalTicks;
    long remaining = targetTicks - (clock.ElapsedTicks - frameStart);
    if (remaining > 0)
    {
        int ms = (int)(remaining * 1000 / Stopwatch.Frequency);
        // Background cadence waits on the event loop, not a blind sleep: a completed asset load
        // (App.Post wakes the wait) or the click refocusing the window ends it immediately
        // instead of after up to the whole background interval.
        if (throttled) app.WaitForWork(ms);
        else if (ms > 0) Thread.Sleep(ms);
    }
}

CloseSession();
return 0;
