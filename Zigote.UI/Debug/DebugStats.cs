using System.Diagnostics;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Native;

namespace Zigote.UI.Debug;

/// <summary>
///     Shared, cheap, once-per-frame sample of frame/CPU/memory/renderer health. The overlay calls
///     <see cref="Sample" /> each frame while the menu (or compact badge) is visible; the Overview /
///     Profiler panels and the compact stats badge all read these fields, so the numbers are computed
///     once rather than per panel. Frame times also feed <see cref="DebugProfiler" />'s history ring.
/// </summary>
public static class DebugStats
{
    private const int FpsRing = 120;
    private static readonly float[] FrameTimes = new float[FpsRing];
    private static float _fpsTimer;

    private static readonly Process Proc = Process.GetCurrentProcess();
    private static TimeSpan _lastCpu = SafeCpuTime();
    private static float _metricTimer = 1f; // force first-frame sample
    private static float _statTimer = 1f;

    public static float Fps { get; private set; } = 60f;
    public static float FpsMin { get; private set; } = 60f;
    public static float FpsMax { get; private set; } = 60f;
    public static float FrameMs { get; private set; }
    public static float MemMb { get; private set; }
    public static float GcMb { get; private set; }
    public static float CpuPct { get; private set; }
    public static int Gen0Collections { get; private set; }
    public static int Gen1Collections { get; private set; }
    public static int Gen2Collections { get; private set; }
    public static ZgEngineStats Engine { get; private set; }
    public static bool EngineOk { get; private set; }

    /// <summary>
    ///     Frames whose dt overran 1.5× the frame budget (missed at least one vsync deadline) —
    ///     the jank the user actually perceives. <see cref="AnimatedJankFrames" /> is the subset
    ///     that landed while something was animating (a scroll ease, a fling, a transition), where
    ///     a missed deadline is a visible stutter rather than an invisible idle hiccup.
    /// </summary>
    public static long JankFrames { get; private set; }

    public static long AnimatedJankFrames { get; private set; }

    /// <summary>Total working (non-idle) frames sampled — the denominator for the jank rates above.</summary>
    public static long TotalFrames { get; private set; }

    private static readonly Dictionary<string, long> JankCausesDict = new();

    /// <summary>
    ///     Jank frames attributed to the longest top-level <see cref="Profiler" /> scope of the
    ///     frame that overran (Chrome's scroll-jank work: fixing jank starts with knowing WHICH
    ///     stage misses the deadline). Empty unless the host brackets frames with
    ///     <see cref="Profiler.BeginFrame" />/<see cref="Profiler.EndFrame" /> (the editor does).
    /// </summary>
    public static IReadOnlyDictionary<string, long> JankCauses => JankCausesDict;

    /// <summary>Paint-command counts from the last frame (set by the frame loop after painting).</summary>
    public static int UiPaintCommands { get; set; }

    public static int OverlayPaintCommands { get; set; }

    /// <summary>Most recent per-frame deltas (newest at <see cref="FrameWriteIndex" />-1), for graphs.</summary>
    public static IReadOnlyList<float> FrameRing => FrameTimes;

    public static int FrameWriteIndex { get; private set; }

    public static int FrameRingCount => Math.Min(val1: FrameWriteIndex, val2: FpsRing);

    /// <summary>
    ///     <see cref="Process.TotalProcessorTime" /> throws PlatformNotSupported on iOS/Android.
    ///     The per-sample reads are already try/guarded; this keeps the TYPE INITIALIZER from
    ///     throwing there too (a cctor exception takes the whole app down, and it did — the
    ///     CPU% readout just stays 0 on those platforms).
    /// </summary>
    private static TimeSpan SafeCpuTime()
    {
        try
        {
            return Proc.TotalProcessorTime;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    ///     Blame the just-measured overrun on the longest top-level scope of the completed frame
    ///     (dt measured at frame start covers exactly the frame whose events sit in
    ///     <see cref="Profiler.LastFrame" />). Silently a no-op when the host never brackets
    ///     frames — LastFrame stays empty then.
    /// </summary>
    private static void NoteJankCause()
    {
        var events = Profiler.LastFrame;
        long best = 0;
        int bestId = -1;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Depth != 0 || e.DurationTicks <= best) continue;
            best = e.DurationTicks;
            bestId = e.NameId;
        }

        if (bestId < 0) return;
        string name = Profiler.NameOf(bestId);
        JankCausesDict[name] = JankCausesDict.GetValueOrDefault(name) + 1;
    }

    /// <summary>
    ///     Fired at the end of every <see cref="Sample" /> with the frame's dt. External diagnostics
    ///     (e.g. the charts-powered debug panels in <c>Zigote.UI.Charts</c>) subscribe to build their
    ///     own history rings without Zigote.UI referencing them.
    /// </summary>
    public static event Action<float>? Sampled;

    public static void Sample(float dt, float frameBudget = 0f, bool animating = false,
        bool idle = false)
    {
        FrameTimes[FrameWriteIndex % FpsRing] = dt;
        FrameWriteIndex++;
        DebugProfiler.RecordFrame(dt * 1000f);

        // A frame that slept in WaitEvents wakes with a long dt by design — not jank, and not a
        // "working frame" for the rate's denominator either.
        if (!idle)
        {
            TotalFrames++;
            if (frameBudget > 0f && dt > 1.5f * frameBudget)
            {
                JankFrames++;
                if (animating) AnimatedJankFrames++;
                NoteJankCause();
            }
        }

        _fpsTimer += dt;
        if (_fpsTimer >= 0.25f)
        {
            int count = Math.Min(val1: FrameWriteIndex, val2: FpsRing);
            float sum = 0f, worst = 0f, best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float t = FrameTimes[i];
                sum += t;
                worst = MathF.Max(x: worst, y: t);
                best = MathF.Min(x: best, y: t);
            }

            Fps = sum > 0f ? count / sum : 0f;
            FrameMs = count > 0 ? sum / count * 1000f : 0f;
            FpsMin = worst > 0f ? 1f / worst : 0f;
            FpsMax = best > 0f ? 1f / best : 0f;
            _fpsTimer = 0f;
        }

        _metricTimer += dt;
        if (_metricTimer >= 1f)
        {
            try
            {
                Proc.Refresh();
                MemMb = Proc.WorkingSet64 / (1024f * 1024f);
                var cpu = Proc.TotalProcessorTime;
                double elapsed = (cpu - _lastCpu).TotalMilliseconds;
                CpuPct = (float)(elapsed / (_metricTimer * 1000.0 * Environment.ProcessorCount) *
                                 100.0);
                _lastCpu = cpu;
            }
            catch
            {
                /* ignore */
            }

            GcMb = GC.GetTotalMemory(false) / (1024f * 1024f);
            Gen0Collections = GC.CollectionCount(0);
            Gen1Collections = GC.CollectionCount(1);
            Gen2Collections = GC.CollectionCount(2);
            _metricTimer = 0f;
        }

        _statTimer += dt;
        if (_statTimer >= 0.4f)
        {
            try
            {
                var e = ZigoteEngine.Instance;
                if (e is not null)
                {
                    Engine = e.GetEngineStats();
                    EngineOk = true;
                }
                else
                    EngineOk = false;
            }
            catch
            {
                EngineOk = false;
            }

            _statTimer = 0f;
        }

        Sampled?.Invoke(dt);
    }
}
