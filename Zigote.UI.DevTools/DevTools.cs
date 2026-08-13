using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Native;
using Zigote.Core.State;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Panels;
using Zigote.UI.Host;
using Zigote.UI.Widgets;

namespace Zigote.UI.DevTools;

/// <summary>
///     The devtools package entry point. A host installs the professional, widget + chart based debug
///     overlay with one line after constructing its <see cref="App" />:
///     <code>DevTools.Install(app, DevToolsProfile.ThreeD);</code>
///     It registers the built-in General / 2D·UI / 3D·Render panels (gated by the
///     <see cref="DevToolsProfile" />), the diagnostics commands + variables, and wires the App seams
///     (Shift+D toggle, per-frame refresh, continuous-frame source). <c>Zigote.UI</c> deliberately has
///     no
///     knowledge of this assembly, so installation is the host's opt-in.
/// </summary>
public static class DevTools
{
    /// <summary>The controller of the most-recently installed host (used by <see cref="Register" />).</summary>
    public static DevToolsController? Current { get; private set; }

    /// <summary>
    ///     Install the devtools overlay onto <paramref name="app" />. Idempotent per app. Pass
    ///     <see cref="DevToolsProfile.ThreeD" /> for a 3D game/editor, <see cref="DevToolsProfile.TwoD" />
    ///     for a pure UI/2D app, or leave the default <see cref="DevToolsProfile.Auto" /> to resolve from
    ///     the live renderer.
    /// </summary>
    public static DevToolsController Install(App app,
        DevToolsProfile profile = DevToolsProfile.Auto)
    {
        if (app.OnToggleDevTools is not null && Current is { } existing && existing.App == app)
            return existing; // already installed on this app

        DevChartData.Install();

        var controller = new DevToolsController(app: app, profile: profile);
        Current = controller;

        RegisterBuiltinPanels(controller);

        var panel = new DevToolsPanel(controller);
        controller.AttachPanel(panel);

        app.OnToggleDevTools = controller.TogglePanel;
        app.OnToggleDevCompact = controller.ToggleCompact;
        app.FrameTick += controller.Tick;
        app.AddContinuousFrameSource(() => controller.WantsContinuousFrame);
        app.PushOverlay(controller.Layer);

        RegisterCommands(app: app, c: controller);
        RegisterVariables(app: app, c: controller);

        // Debug affordance: open the panel on boot (e.g. for a screenshot / smoke run) without a keypress.
        if (Environment.GetEnvironmentVariable("ZIGOTE_DEVTOOLS_OPEN") == "1")
            controller.SetPanelOpen(true);
        // Demo/smoke aid: auto-advance through every tab so a headless run visits the whole overlay.
        if (Environment.GetEnvironmentVariable("ZIGOTE_DEVTOOLS_CYCLE") == "1")
        {
            controller.AutoCycle = true;
            controller.SetPanelOpen(true);
        }

        return controller;
    }

    /// <summary>Register a host-specific panel (scene, physics, gameplay) into the current overlay.</summary>
    public static void Register(IDevPanel panel) => Current?.Register(panel);

    private static void RegisterBuiltinPanels(DevToolsController c)
    {
        // General — engine-wide health, shown for every app type.
        c.Register(new OverviewPanel());
        c.Register(new PerformancePanel());
        c.Register(new MemoryPanel());
        c.Register(new GpuPanel());
        c.Register(new ReactivePanel());
        c.Register(new LogsPanel());
        c.Register(new ConsolePanel());
        c.Register(new VariablesPanel());

        // 2D · UI — always available.
        c.Register(new UiInspectorPanel(c));
        c.Register(new UiPaintPanel());
        c.Register(new SemanticsPanel(c.App));

        // 3D · Render — only surfaced when the profile resolves to 3D (the category tab is hidden
        // otherwise), but registered unconditionally so an Auto host that starts 2D and later renders
        // 3D lights the tab up without a re-install.
        c.Register(new PipelinePanel());
        c.Register(new RendererPanel());
    }

    // ── Commands (ported from the old DebugMenuDefaults) ──

    private static void RegisterCommands(App app, DevToolsController c)
    {
        DebugCommands.RegisterCoreDefaults();

        DebugCommands.Register(
            name: "popout",
            description: "Open the devtools in their own window",
            execute: _ =>
            {
                c.OpenWindow();
                return DebugCommandResult.Success("devtools window opened");
            },
            category: "app"
        );
        DebugCommands.Register(
            name: "fullscreen",
            description: "Toggle the fullscreen devtools panel",
            execute: _ =>
            {
                c.ToggleFullscreen();
                return DebugCommandResult.Success(c.Fullscreen ? "fullscreen" : "docked");
            },
            category: "app"
        );

        DebugCommands.Register(
            name: "menu",
            description: "Toggle the devtools panel",
            action: app.ToggleDebugPanel,
            category: "app"
        );
        DebugCommands.Register(
            name: "compact",
            description: "Toggle the compact stats overlay",
            action: app.ToggleCompactStats,
            category: "app"
        );
        DebugCommands.Register(
            name: "quit",
            description: "Exit the application",
            action: app.RequestQuit,
            category: "app"
        );
        DebugCommands.Register(
            name: "gc",
            description: "Force a full GC and report the heap",
            execute: _ =>
            {
                long before = GC.GetTotalMemory(false);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long after = GC.GetTotalMemory(true);
                return DebugCommandResult.Success(
                    $"heap {before / 1048576f:F1} → {after / 1048576f:F1} MB"
                );
            },
            category: "app"
        );
        DebugCommands.Register(
            name: "profile",
            description: "Capture N frames of CPU profiling to profile_capture.json",
            execute: args =>
            {
                int frames = args.Length > 0 && int.TryParse(s: args[0], result: out int n)
                    ? n
                    : 120;
                Profiler.Capture(frames: frames, outputPath: "profile_capture.json");
                return DebugCommandResult.Success($"capturing {frames} frames…");
            },
            category: "app",
            usage: "profile [frames]"
        );
    }

    // ── Variables (ported from the old DebugMenuDefaults) ──

    /// <summary>
    ///     The reactive graph's live counters, as read-only variables (console: <c>get reactive.runs</c>).
    ///     Watch the per-second delta while the app sits idle: a climbing <c>runs</c> or
    ///     <c>watch_rebuilds</c> with nothing on screen changing is the churn the architecture doc's
    ///     "rebuild counter" is there to catch, and <c>deferred</c> is the cross-thread backlog.
    /// </summary>
    private static void RegisterReactiveCounters()
    {
        Counter(
            name: "reactive.writes",
            get: () => Reactive.Writes,
            description: "Signal writes + trigger fires committed"
        );
        Counter(
            name: "reactive.runs",
            get: () => Reactive.Runs,
            description: "Computed recomputes + effect runs"
        );
        Counter(
            name: "reactive.deferred",
            get: () => Reactive.PendingDeferred,
            description: "Deferred effects parked at the last frame's drain"
        );
        Counter(
            name: "ui.watch_rebuilds",
            get: () => Watch.Rebuilds,
            description: "Watch subtree swaps (excl. first build)"
        );

        static void Counter(string name, Func<long> get, string description)
        {
            DebugVariables.Register(
                new DebugVariable {
                    Name = name,
                    Category = "reactive",
                    Description = description,
                    Type = DebugVarType.Int,
                    Getter = () => get(),
                }
            );
        }
    }

    /// <summary>
    ///     Texture residency, as read-only variables (console: <c>get gpu.textures</c>).
    ///     <para>
    ///         Texture handles are caller-owned and nothing else frees them, so a widget or panel that
    ///         loads one and forgets to release it strands it for the process's lifetime — and until
    ///         now nothing showed that anywhere. Watch the count while browsing an asset folder or
    ///         scrolling a gallery: it should come back down. A number that only climbs is a missing
    ///         <c>ReleaseTexture</c>, and this is the cheapest place to see it.
    ///     </para>
    /// </summary>
    private static void RegisterTextureCounters()
    {
        Counter(
            name: "gpu.textures",
            description: "Resident textures (handles the engine still holds)"
        );
        Counter(name: "gpu.texture_bytes", description: "GPU bytes held by resident textures");
        Counter(
            name: "gpu.texture_cpu_bytes",
            description: "CPU-side decoded bytes held by resident textures"
        );

        static void Counter(string name, string description)
        {
            DebugVariables.Register(
                new DebugVariable {
                    Name = name,
                    Category = "gpu",
                    Description = description,
                    Type = DebugVarType.Int,
                    Getter = () =>
                    {
                        ZigoteEngine.GetImageStats(
                            count: out int count,
                            cpuBytes: out long cpu,
                            gpuBytes: out long gpu
                        );
                        return name switch {
                            "gpu.textures" => count,
                            "gpu.texture_bytes" => gpu,
                            _ => cpu,
                        };
                    },
                }
            );
        }
    }

    private static void RegisterVariables(App app, DevToolsController c)
    {
        RegisterReactiveCounters();
        RegisterTextureCounters();

        DebugVariables.RegisterBool(
            name: "app.continuous",
            getter: () => app.ContinuousUpdate,
            setter: v => app.ContinuousUpdate = v,
            category: "app",
            description: "Force the frame loop to render every frame"
        );
        DebugVariables.RegisterBool(
            name: "app.force_continuous",
            getter: () => app.ForceContinuousRender,
            setter: v => app.ForceContinuousRender = v,
            category: "app",
            description: "Render every frame for FPS testing (independent of play)"
        );
        DebugVariables.RegisterInt(
            name: "app.fps_limit",
            getter: () => app.FrameRateLimit,
            setter: v => app.FrameRateLimit = Math.Max(val1: 0, val2: v),
            min: 0,
            max: 1000,
            category: "app",
            description: "Cap the render loop to N fps (0 = unlimited)"
        );
        DebugVariables.RegisterBool(
            name: "app.vsync",
            getter: () => app.VSync,
            setter: v => app.VSync = v,
            category: "app",
            description: "Swapchain vsync (off = uncapped present, wgpu only)"
        );
        DebugVariables.RegisterBool(
            name: "render.partial_repaint",
            getter: () => app.PartialRepaintEnabled,
            setter: v => app.PartialRepaintEnabled = v,
            category: "render",
            description:
            "Sub-rectangle damage repaint (GPU scissor) — off forces a full clear every frame"
        );

        DebugVariables.RegisterBool(
            name: "ui.repaint_rainbow",
            getter: () => c.ShowRepaintRainbow,
            setter: v => c.ShowRepaintRainbow = v,
            category: "ui"
        );
        DebugVariables.RegisterBool(
            name: "ui.layout_bounds",
            getter: () => c.ShowLayoutBounds,
            setter: v => c.ShowLayoutBounds = v,
            category: "ui"
        );
        DebugVariables.RegisterBool(
            name: "ui.overflow",
            getter: () => c.ShowOverflow,
            setter: v => c.ShowOverflow = v,
            category: "ui"
        );

        DebugVariables.RegisterEnum(
            name: "render.debug_view",
            getter: () => (DebugView)(int)ReadRender().DebugView,
            setter: dv => ModifyRender((ref s) => s.DebugView = (int)dv),
            category: "render",
            description: "G-buffer / lighting visualisation channel"
        );
        DebugVariables.RegisterBool(
            name: "render.diagnostic",
            getter: () => ReadRender().DiagnosticMode != 0f,
            setter: v => ModifyRender((ref s) => s.DiagnosticMode = v ? 1f : 0f),
            category: "render"
        );
        DebugVariables.RegisterFloat(
            name: "render.exposure",
            getter: () => ReadRender().Exposure,
            setter: v => ModifyRender((ref s) => s.Exposure = v),
            min: 0.2f,
            max: 3f,
            category: "render"
        );
    }

    private static ZgRenderSettings3D ReadRender()
    {
        try
        {
            return ZigoteEngine.Instance?.GetRenderSettings3D() ?? default;
        }
        catch
        {
            return default;
        }
    }

    private static void ModifyRender(RenderMutate f)
    {
        try
        {
            var e = ZigoteEngine.Instance;
            if (e is null) return;
            var s = e.GetRenderSettings3D();
            f(ref s);
            e.SetRenderSettings3D(s);
        }
        catch
        {
            /* engine not ready */
        }
    }

    private delegate void RenderMutate(ref ZgRenderSettings3D s);
}
