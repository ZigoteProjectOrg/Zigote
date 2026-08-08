using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Native;
using Zigote.Core.State;
using Zigote.UI.Widgets;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Panels;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools;

/// <summary>
///     The devtools package entry point. A host installs the professional, widget + chart based debug
///     overlay with one line after constructing its <see cref="App" />:
///     <code>DevTools.Install(app, DevToolsProfile.ThreeD);</code>
///     It registers the built-in General / 2D·UI / 3D·Render panels (gated by the
///     <see cref="DevToolsProfile" />), the diagnostics commands + variables, and wires the App seams
///     (Shift+D toggle, per-frame refresh, continuous-frame source). <c>Zigote.UI</c> deliberately has no
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

        var controller = new DevToolsController(app, profile);
        Current = controller;

        RegisterBuiltinPanels(controller);

        var panel = new DevToolsPanel(controller);
        controller.AttachPanel(panel);

        app.OnToggleDevTools = controller.TogglePanel;
        app.OnToggleDevCompact = controller.ToggleCompact;
        app.FrameTick += controller.Tick;
        app.AddContinuousFrameSource(() => controller.WantsContinuousFrame);
        app.PushOverlay(controller.Layer);

        RegisterCommands(app, controller);
        RegisterVariables(app, controller);

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
    public static void Register(IDevPanel panel)
    {
        Current?.Register(panel);
    }

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
            "popout",
            "Open the devtools in their own window",
            _ =>
            {
                c.OpenWindow();
                return DebugCommandResult.Success("devtools window opened");
            },
            "app"
        );
        DebugCommands.Register(
            "fullscreen",
            "Toggle the fullscreen devtools panel",
            _ =>
            {
                c.ToggleFullscreen();
                return DebugCommandResult.Success(c.Fullscreen ? "fullscreen" : "docked");
            },
            "app"
        );

        DebugCommands.Register(
            "menu",
            "Toggle the devtools panel",
            app.ToggleDebugPanel,
            "app"
        );
        DebugCommands.Register(
            "compact",
            "Toggle the compact stats overlay",
            app.ToggleCompactStats,
            "app"
        );
        DebugCommands.Register(
            "quit",
            "Exit the application",
            app.RequestQuit,
            "app"
        );
        DebugCommands.Register(
            "gc",
            "Force a full GC and report the heap",
            _ =>
            {
                var before = GC.GetTotalMemory(false);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                var after = GC.GetTotalMemory(true);
                return DebugCommandResult.Success(
                    $"heap {before / 1048576f:F1} → {after / 1048576f:F1} MB"
                );
            },
            "app"
        );
        DebugCommands.Register(
            "profile",
            "Capture N frames of CPU profiling to profile_capture.json",
            args =>
            {
                var frames = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 120;
                Profiler.Capture(frames, "profile_capture.json");
                return DebugCommandResult.Success($"capturing {frames} frames…");
            },
            "app",
            "profile [frames]"
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
            "reactive.writes",
            () => Reactive.Writes,
            "Signal writes + trigger fires committed"
        );
        Counter("reactive.runs", () => Reactive.Runs, "Computed recomputes + effect runs");
        Counter(
            "reactive.deferred",
            () => Reactive.PendingDeferred,
            "Deferred effects parked at the last frame's drain"
        );
        Counter(
            "ui.watch_rebuilds",
            () => Watch.Rebuilds,
            "Watch subtree swaps (excl. first build)"
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
        Counter("gpu.textures", "Resident textures (handles the engine still holds)");
        Counter("gpu.texture_bytes", "GPU bytes held by resident textures");
        Counter("gpu.texture_cpu_bytes", "CPU-side decoded bytes held by resident textures");

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
                        ZigoteEngine.GetImageStats(out var count, out var cpu, out var gpu);
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
            "app.continuous",
            () => app.ContinuousUpdate,
            v => app.ContinuousUpdate = v,
            "app",
            "Force the frame loop to render every frame"
        );
        DebugVariables.RegisterBool(
            "app.force_continuous",
            () => app.ForceContinuousRender,
            v => app.ForceContinuousRender = v,
            "app",
            "Render every frame for FPS testing (independent of play)"
        );
        DebugVariables.RegisterInt(
            "app.fps_limit",
            () => app.FrameRateLimit,
            v => app.FrameRateLimit = Math.Max(0, v),
            0,
            1000,
            "app",
            "Cap the render loop to N fps (0 = unlimited)"
        );
        DebugVariables.RegisterBool(
            "app.vsync",
            () => app.VSync,
            v => app.VSync = v,
            "app",
            "Swapchain vsync (off = uncapped present, wgpu only)"
        );
        DebugVariables.RegisterBool(
            "render.partial_repaint",
            () => app.PartialRepaintEnabled,
            v => app.PartialRepaintEnabled = v,
            "render",
            "Sub-rectangle damage repaint (GPU scissor) — off forces a full clear every frame"
        );

        DebugVariables.RegisterBool(
            "ui.repaint_rainbow",
            () => c.ShowRepaintRainbow,
            v => c.ShowRepaintRainbow = v,
            "ui"
        );
        DebugVariables.RegisterBool(
            "ui.layout_bounds",
            () => c.ShowLayoutBounds,
            v => c.ShowLayoutBounds = v,
            "ui"
        );
        DebugVariables.RegisterBool(
            "ui.overflow",
            () => c.ShowOverflow,
            v => c.ShowOverflow = v,
            "ui"
        );

        DebugVariables.RegisterEnum(
            "render.debug_view",
            () => (DebugView)(int)ReadRender().DebugView,
            dv => ModifyRender((ref ZgRenderSettings3D s) => s.DebugView = (int)dv),
            "render",
            "G-buffer / lighting visualisation channel"
        );
        DebugVariables.RegisterBool(
            "render.diagnostic",
            () => ReadRender().DiagnosticMode != 0f,
            v => ModifyRender((ref ZgRenderSettings3D s) => s.DiagnosticMode = v ? 1f : 0f),
            "render"
        );
        DebugVariables.RegisterFloat(
            "render.exposure",
            () => ReadRender().Exposure,
            v => ModifyRender((ref ZgRenderSettings3D s) => s.Exposure = v),
            0.2f,
            3f,
            "render"
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

    private delegate void RenderMutate(ref ZgRenderSettings3D s);

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
}