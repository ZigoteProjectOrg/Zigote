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

    /// <summary>Paint-command counts from the last frame (set by the frame loop after painting).</summary>
    public static int UiPaintCommands { get; set; }

    public static int OverlayPaintCommands { get; set; }

    /// <summary>Most recent per-frame deltas (newest at <see cref="FrameWriteIndex" />-1), for graphs.</summary>
    public static IReadOnlyList<float> FrameRing => FrameTimes;

    public static int FrameWriteIndex { get; private set; }

    public static int FrameRingCount => Math.Min(FrameWriteIndex, FpsRing);

    /// <summary>
    ///     Fired at the end of every <see cref="Sample" /> with the frame's dt. External diagnostics
    ///     (e.g. the charts-powered debug panels in <c>Zigote.UI.Charts</c>) subscribe to build their
    ///     own history rings without Zigote.UI referencing them.
    /// </summary>
    public static event Action<float>? Sampled;

    public static void Sample(float dt)
    {
        FrameTimes[FrameWriteIndex % FpsRing] = dt;
        FrameWriteIndex++;
        DebugProfiler.RecordFrame(dt * 1000f);

        _fpsTimer += dt;
        if (_fpsTimer >= 0.25f)
        {
            var count = Math.Min(FrameWriteIndex, FpsRing);
            float sum = 0f, worst = 0f, best = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var t = FrameTimes[i];
                sum += t;
                worst = MathF.Max(worst, t);
                best = MathF.Min(best, t);
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
                var elapsed = (cpu - _lastCpu).TotalMilliseconds;
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
                {
                    EngineOk = false;
                }
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
