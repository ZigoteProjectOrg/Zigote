using Zigote.Core.State;
using Zigote.UI.Debug;
using Zigote.UI.Host;
using Zigote.UI.Widgets;

namespace Zigote.UI.DevTools.Diagnostics;

/// <summary>
///     History rings behind the charts-powered devtools panels, fed from
///     <see cref="DebugStats.Sampled" /> (fired once per rendered frame — <see cref="App" /> samples
///     unconditionally on the main window). Sampling runs whether or not the panel is open, so opening a
///     panel shows history rather than a blank chart. Each ring has its own cadence chosen to match how
///     fast its source actually changes (engine counters at 0.4 s, CPU / memory / GPU at 1 s), so pushes
///     are effectively free.
///     <para>
///         <see cref="Revision" /> bumps on every push wave; panels relayout their charts (and shift the
///         rolling x-window) only when it changes, keeping the per-frame cost at "paint cached geometry".
///     </para>
/// </summary>
public static class DevChartData
{
    private const float FastPeriod = 0.25f; // fps + engine counters → 60 s window at 240 samples
    private const float SlowPeriod = 1.0f; // cpu / memory / GC / GPU → 2 min window at 120 samples

    private static bool _installed;
    private static float _fastTimer = FastPeriod;
    private static float _slowTimer = SlowPeriod;

    private static int _lastGen0, _lastGen1, _lastGen2;
    private static long _lastAllocBytes;
    private static ulong _lastFrameIndex;
    private static long _lastRuns, _lastRebuilds;

    // ── Fast rings (0.25 s cadence, 60 s window) ─────────────────────────────
    public static TimeSeriesRing Fps { get; } = new(240);
    public static TimeSeriesRing FrameMs { get; } = new(240);
    public static TimeSeriesRing DrawCalls { get; } = new(240);
    public static TimeSeriesRing Triangles { get; } = new(240);
    public static TimeSeriesRing VisibleObjects { get; } = new(240);
    public static TimeSeriesRing RenderPasses { get; } = new(240);
    public static TimeSeriesRing UiCommands { get; } = new(240);
    public static TimeSeriesRing OverlayCommands { get; } = new(240);

    /// <summary>Reaction bodies run per second — computed recomputes plus effect runs.</summary>
    public static TimeSeriesRing ReactionRuns { get; } = new(240);

    /// <summary><see cref="Watch" /> subtree swaps per second — the UI-visible share of the above.</summary>
    public static TimeSeriesRing WatchRebuilds { get; } = new(240);

    // ── Slow rings (1 s cadence, 2 min window) ───────────────────────────────
    public static TimeSeriesRing WorkingSetMb { get; } = new(120);
    public static TimeSeriesRing GcHeapMb { get; } = new(120);
    public static TimeSeriesRing UnmanagedMb { get; } = new(120);
    public static TimeSeriesRing CpuPct { get; } = new(120);
    public static TimeSeriesRing Gen0PerSec { get; } = new(120);
    public static TimeSeriesRing Gen1PerSec { get; } = new(120);
    public static TimeSeriesRing Gen2PerSec { get; } = new(120);

    /// <summary>Managed allocation rate in MB/s (GC.GetTotalAllocatedBytes delta).</summary>
    public static TimeSeriesRing AllocMbPerSec { get; } = new(120);

    // ── GPU memory rings (1 s cadence) ───────────────────────────────────────
    public static TimeSeriesRing GpuBufferMb { get; } = new(120);
    public static TimeSeriesRing GpuTextureMb { get; } = new(120);
    public static TimeSeriesRing GpuTotalMb { get; } = new(120);

    /// <summary>Session clock the rings are stamped with (seconds since install).</summary>
    public static float Time { get; private set; }

    /// <summary>Bumped whenever any ring received samples; chart panels relayout on change.</summary>
    public static int Revision { get; private set; }

    /// <summary>
    ///     True when the native renderer produced a 3D frame since the previous engine sample —
    ///     UI-only frames leave the render counters frozen at their last 3D values (native quirk), so
    ///     panels flag the counters as stale instead of showing misleading flat lines.
    /// </summary>
    public static bool Rendering3D { get; private set; }

    /// <summary>Hook into <see cref="DebugStats.Sampled" />. Idempotent.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        DebugStats.Sampled += OnSample;
    }

    private static void OnSample(float dt)
    {
        Time += dt;

        _fastTimer += dt;
        if (_fastTimer >= FastPeriod)
        {
            var elapsed = _fastTimer;
            _fastTimer = 0f;
            Fps.Push(Time, DebugStats.Fps);
            FrameMs.Push(Time, DebugStats.FrameMs);
            UiCommands.Push(Time, DebugStats.UiPaintCommands);
            OverlayCommands.Push(Time, DebugStats.OverlayPaintCommands);

            // Rates, not totals: a monotonic counter drawn as a line only ever slopes upward.
            var runs = Reactive.Runs;
            var rebuilds = Watch.Rebuilds;
            ReactionRuns.Push(Time, (float)((runs - _lastRuns) / elapsed));
            WatchRebuilds.Push(Time, (float)((rebuilds - _lastRebuilds) / elapsed));
            _lastRuns = runs;
            _lastRebuilds = rebuilds;

            if (DebugStats.EngineOk)
            {
                var e = DebugStats.Engine;
                Rendering3D = e.FrameIndex != _lastFrameIndex;
                _lastFrameIndex = e.FrameIndex;
                DrawCalls.Push(Time, e.DrawCalls);
                Triangles.Push(Time, e.Triangles);
                VisibleObjects.Push(Time, e.VisibleObjects);
                RenderPasses.Push(Time, e.RenderPasses);
            }
            else
            {
                Rendering3D = false;
            }

            Revision++;
        }

        _slowTimer += dt;
        if (_slowTimer >= SlowPeriod)
        {
            var elapsed = _slowTimer;
            _slowTimer = 0f;
            var heap = DebugStats.GcMb;
            var ws = DebugStats.MemMb;
            WorkingSetMb.Push(Time, ws);
            GcHeapMb.Push(Time, heap);
            // Working set includes the managed heap plus every native allocation (wgpu, SDL, images,
            // the CLR runtime itself). The remainder after the managed heap is the "unmanaged" slice.
            UnmanagedMb.Push(Time, MathF.Max(0f, ws - heap));
            CpuPct.Push(Time, DebugStats.CpuPct);

            Gen0PerSec.Push(Time, (DebugStats.Gen0Collections - _lastGen0) / elapsed);
            Gen1PerSec.Push(Time, (DebugStats.Gen1Collections - _lastGen1) / elapsed);
            Gen2PerSec.Push(Time, (DebugStats.Gen2Collections - _lastGen2) / elapsed);
            _lastGen0 = DebugStats.Gen0Collections;
            _lastGen1 = DebugStats.Gen1Collections;
            _lastGen2 = DebugStats.Gen2Collections;

            var alloc = GC.GetTotalAllocatedBytes();
            if (_lastAllocBytes > 0)
                AllocMbPerSec.Push(Time, (alloc - _lastAllocBytes) / elapsed / (1024f * 1024f));
            _lastAllocBytes = alloc;

            if (DebugStats.EngineOk)
            {
                var e = DebugStats.Engine;
                var bufMb = e.GpuBufferMemory / (1024f * 1024f);
                var texMb = e.GpuTextureMemory / (1024f * 1024f);
                GpuBufferMb.Push(Time, bufMb);
                GpuTextureMb.Push(Time, texMb);
                GpuTotalMb.Push(Time, bufMb + texMb);
            }

            Revision++;
        }
    }
}