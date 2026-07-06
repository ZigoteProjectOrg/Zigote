using System.Diagnostics;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Editor;
using Zigote.Editor.Export;
using Zigote.Editor.Scene;
using Zigote.Editor.Settings;
using Zigote.Editor.Vfx;
using Zigote.Editor.Widgets;
using Zigote.Runtime.Scene;
using Zigote.Runtime.Vfx;
using Zigote.UI.DevTools;
using Zigote.UI.Host;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Menu;

// Headless game export (no window, no engine init) — used by CI and scripting.
var cli = Environment.GetCommandLineArgs();
if (cli.Length > 1 && cli[1] == "--export")
    return await ExportCli.RunAsync(cli);

const string appName = "Zigote Editor";

var config = EditorConfig.Load();

// Tee stdout into the editor Console panel before the engine starts logging.
EditorLog.CaptureConsole();

// The runtime resolves VFX emitter assets through this seam: the editor compiles node graphs live,
// while an exported player reads the baked JSON instead (same pattern as Physics.Backend).
VfxAssets.GraphCompiler = n => VfxNodeEditor.Compile(n).Asset;

using var app = new App(appName, 1280, 800);

// Editor preferences (theme mode, fonts, vsync) — resolved from editor.json and applied live by
// the Settings window. Theme mode "system" follows the OS appearance (SystemThemeChanged below).
var prefs = new EditorPreferences(app, config);
var theme = prefs.ResolveTheme();
app.Theme = theme;
prefs.ApplyAtBoot();
DevTools.Install(app, DevToolsProfile.ThreeD);

// Dev tooling: when ZIGOTE_SHOT is set the native engine dumps a one-shot framebuffer
// capture at frame ZIGOTE_SHOT_FRAME. Force continuous rendering so the frame counter
// reliably advances to that frame even on an otherwise-static scene. ForceContinuousRender
// (not ContinuousUpdate, which the toolbar transport owns — it would clobber this) also
// bypasses the viewport's change-gated 3D render, so every frame is a real render.
var shotMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZIGOTE_SHOT"));
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
    Config = config,
    OpenSettings = settingsHost.Open,
};

void CloseSession()
{
    dockWindows.CloseAllForRebuild(); // floating panels belong to the closing shell
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
            s,
            theme,
            app,
            actions
        );
        app.Root = layoutWidget;
        layoutWidget.ApplyEditorFontPreferences(config);
        if (layoutWidget.Dock is { } dock)
        {
            dockWindows.SetMain(dock, theme);
            foreach (var id in floating) dock.OpenPanel(id);
        }
    }
    else
    {
        ShowWelcome();
    }

    settingsHost.ApplyTheme();
    app.RequestPaint();
}

prefs.ThemeChanged += RebuildShell;
prefs.EditorFontChanged += () => layoutWidget?.ApplyEditorFontPreferences(config);
app.SystemThemeChanged += _ => prefs.OnSystemThemeChanged();

void ShowWelcome()
{
    CloseSession();
    // The welcome screen is a static 2D surface — let the idle event-wait gate throttle it.
    app.ContinuousUpdate = false;
    app.Root = new WelcomeScreen(
        app,
        theme,
        config,
        OpenProject
    );
    // Replace any project menu with a minimal welcome menu (native bar only; the
    // in-window welcome screen already has on-screen New/Open buttons).
    NativeMenuBar.TryInstall(
        [
            new AppMenu(
                "File",
                [
                    new ContextMenuItem(
                        "New Project",
                        () => ProjectDialogs.ShowNew(app, theme, OpenProject)
                    ),
                    new ContextMenuItem(
                        "Open Project…",
                        () => ProjectDialogs.ShowOpen(app, OpenProject)
                    ),
                    new ContextMenuItem("", null, true),
                    new ContextMenuItem("Settings…", settingsHost.Open),
                    new ContextMenuItem("", null, true),
                    new ContextMenuItem("Quit", app.RequestQuit, Shortcut: "⌘Q"),
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
        config.Forget(projectFile);
        ShowWelcome();
        return;
    }

    CloseSession();

    var project = ZigoteProject.Load(projectFile);
    var projDir = Path.GetDirectoryName(projectFile) ?? ".";

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

    // Physics-wireframe overlay toggle (debug menu Variables tab / `set render.physics_wireframe 1`).
    // Editor-side debug viz; draws collision shapes over the viewport in edit + play mode.
    DebugVariables.RegisterBool(
        "render.physics_wireframe",
        () => state.ShowPhysicsWireframe,
        v =>
        {
            state.ShowPhysicsWireframe = v;
            app.RequestPaint();
        },
        "3D · Render",
        "Draw physics collision shapes as a wireframe overlay"
    );

    // Demand mesh streaming: load .zmesh meshes within this camera distance off-thread and unload
    // (hide) them beyond it. 0 = off (all meshes stay resident — the default synchronous path). The
    // per-frame asset pump (session.PumpAssets) is always live; only the residency sink gates on this.
    DebugVariables.RegisterFloat(
        "render.stream_distance",
        () => state.StreamDistance,
        v =>
        {
            state.StreamDistance = MathF.Max(0f, v);
            if (!state.StreamingEnabled) state.MeshStreamer.Clear();
            state.InvalidateViewport();
            app.RequestPaint();
        },
        0f,
        1000f,
        "3D · Render",
        "Demand-stream .zmesh meshes within this camera distance (0 = off, all resident)"
    );

    // VFX particles through the native GPU billboard pass vs. the editor 2D-projection overlay. Off by
    // default (the 2D path is always available); clearing it drops any native batches still uploaded.
    DebugVariables.RegisterBool(
        "render.vfx_native",
        () => state.UseNativeVfx,
        v =>
        {
            state.UseNativeVfx = v;
            ZigoteEngine.Instance?.ParticlesClearAll();
            state.InvalidateViewport();
            app.RequestPaint();
        },
        "3D · Render",
        "Render VFX particles with the native GPU billboard pass (additive + alpha blend)"
    );

    // Simulate VFX emitters on the GPU (compute kernel) — the scale path. Clearing it drops the GPU batches.
    DebugVariables.RegisterBool(
        "render.vfx_gpu",
        () => state.UseGpuVfx,
        v =>
        {
            state.UseGpuVfx = v;
            ZigoteEngine.Instance?.ParticlesClearAll();
            state.InvalidateViewport();
            app.RequestPaint();
        },
        "3D · Render",
        "Simulate VFX particles on the GPU (compute) instead of the CPU"
    );

    // Continuously animate VFX emitters in edit mode. Off by default — a static first-frame preview is
    // shown instead (no continuous render). On = the editor renders continuously to animate them.
    DebugVariables.RegisterBool(
        "render.vfx_edit",
        () => state.AnimateEditVfx,
        v =>
        {
            state.AnimateEditVfx = v;
            state.InvalidateViewport();
            app.RequestPaint();
        },
        "3D · Render",
        "Animate VFX emitters live in edit mode (off = static preview)"
    );

    // Native frustum-culling toggle (debug menu Variables / `set render.frustum_cull 0`). On by default;
    // the renderer skips draws whose bounding sphere is outside the camera frustum. Toggling off draws
    // everything — watch the Renderer panel's draw/tri counts drop when it's on and you look away.
    var frustumCull = true;
    DebugVariables.RegisterBool(
        "render.frustum_cull",
        () => frustumCull,
        v =>
        {
            frustumCull = v;
            ZigoteEngine.Instance?.RenderSetFrustumCull(v);
            state.InvalidateViewport();
            app.RequestPaint();
        },
        "3D · Render",
        "Frustum-cull meshes whose bounds fall outside the camera view"
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
    // An editor preference, not a project setting — persisted in the editor config, also reachable
    // from the debug console as `render.editor_reduced`. ZIGOTE_SHOT captures ignore it so golden
    // images never depend on a per-machine preference.
    state.ReducedEditorGraphics = !shotMode && config.ReducedEditorGraphics;
    state.EditorGraphicsChanged += () =>
    {
        config.ReducedEditorGraphics = state.ReducedEditorGraphics;
        config.Save();
        app.RequestPaint();
    };
    DebugVariables.RegisterBool(
        "render.editor_reduced",
        () => state.ReducedEditorGraphics,
        v => state.ReducedEditorGraphics = v,
        "3D · Render",
        "Reduced edit-mode viewport graphics (no TAA/bloom/SSR/DoF); play mode always renders full"
    );

    // Auto-build and hot-watch the project's script assembly if one is configured.
    if (!string.IsNullOrEmpty(project.ScriptProject))
    {
        var scriptProjPath = Path.Combine(projDir, project.ScriptProject);
        if (File.Exists(scriptProjPath))
        {
            state.StartScriptWatcher(scriptProjPath);
            _ = state.BuildScriptsAsync(scriptProjPath);
        }
        else
        {
            Console.WriteLine($"[Zigote] ScriptProject not found: {scriptProjPath}");
        }
    }

    session = state;
    layoutWidget = new EditorLayout(
        state,
        theme,
        app,
        actions
    );
    app.Root = layoutWidget;
    layoutWidget.ApplyEditorFontPreferences(config);
    if (layoutWidget.Dock is { } mainDock) dockWindows.SetMain(mainDock, theme);

    // Edit mode is event-driven: the App idles in WaitEvents until something requests a paint
    // (SceneChanged/SelectionSignal/AssetsChanged above, input, tickers), and the viewport itself
    // change-gates the native 3D render + renders trailing settle frames so TAA/SSGI converge after
    // each change (see ViewportPanel.ShouldRender3D). ContinuousUpdate is owned by the toolbar
    // transport (EditorLayout.SyncTransport: true only while playing) — do not force it here.

    config.RecordOpened(projectFile);
}

// ── Decide the initial screen ───────────────────────────────────────────────
// Explicit CLI arg wins; otherwise reopen the last project (if the preference allows);
// otherwise welcome.
var argsArray = Environment.GetCommandLineArgs();
if (argsArray.Length > 1 && !string.IsNullOrWhiteSpace(argsArray[1]))
    OpenProject(argsArray[1]);
else if (config.ReopenLastProject && !string.IsNullOrEmpty(config.LastProject) &&
         File.Exists(config.LastProject))
    OpenProject(config.LastProject);
else
    ShowWelcome();

// Dev hook: open the Settings window at boot (multi-window smoke testing).
if (Environment.GetEnvironmentVariable("ZIGOTE_OPEN_SETTINGS") == "1")
    settingsHost.Open();

// ── Main loop ────────────────────────────────────────────────────────────────
// Editor renders up to 120 fps for a smoother UI (on 120 Hz / ProMotion displays; vsync still caps
// to the panel's refresh rate). Play-mode game timing is managed by the GameSession itself
// (it advances physics/scripts from dt), so it is independent of this editor render cap.
// A backgrounded editor (window focus lost, not playing) drops to a coarse heartbeat — the common
// edit-in-external-IDE workflow shouldn't burn CPU/GPU on frames nobody sees. Play mode keeps full
// rate in the background so a running game stays live.
const int editorTargetFps = 60;
const int backgroundFps = 10;
var focusedTicks = Stopwatch.Frequency / editorTargetFps;
var backgroundTicks = Stopwatch.Frequency / backgroundFps;
var clock = Stopwatch.StartNew();
var assetFrame = 0L;

while (!app.ShouldQuit)
{
    var frameStart = clock.ElapsedTicks;
    Profiler.BeginFrame();
    using (Profiler.Scope("Frame"))
    {
        using (Profiler.Scope("App.Frame"))
        {
            app.Frame();
        }

        using (Profiler.Scope("Update.Play"))
        {
            session?.TickPlay(app.DeltaTime);
        }

        using (Profiler.Scope("Update.Animation"))
        {
            session?.TickAnimation(app.DeltaTime);
        }

        using (Profiler.Scope("Assets.Pump"))
        {
            if (session?.PumpAssets(assetFrame++) == true)
                app.RequestPaint();
        }
    }

    Profiler.EndFrame();

    // Decide the pace AFTER the frame, so the focus event app.Frame just processed takes effect
    // before we sleep — refocus latency is one 8 ms pad, not a stale ~100 ms background sleep.
    // AnyWindowFocused (not WindowFocused): a focused Settings window must keep full rate — its
    // frames are pumped from this same loop, and typing at the background heartbeat feels broken.
    var throttled = !app.AnyWindowFocused && session is not { IsPlaying: true } &&
                    !app.ForceContinuousRender;
    var targetTicks = throttled ? backgroundTicks : focusedTicks;
    var remaining = targetTicks - (clock.ElapsedTicks - frameStart);
    if (remaining > 0) Thread.Sleep((int)(remaining * 1000 / Stopwatch.Frequency));
}

CloseSession();
return 0;