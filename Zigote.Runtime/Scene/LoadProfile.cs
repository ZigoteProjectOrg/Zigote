using System.Diagnostics;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Lightweight scene-load profiler. Set <c>ZIGOTE_LOAD_PROFILE=1</c> to print a per-phase
///     breakdown (mesh-blob reads + uploads, normal-map decodes, base/MR texture batch, environment).
///     The tick accumulators are always updated — <see cref="Stopwatch.GetTimestamp" /> is cheap — and
///     only formatted/printed when enabled, so this adds no measurable overhead to normal loads.
/// </summary>
internal static class LoadProfile
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("ZIGOTE_LOAD_PROFILE") is { Length: > 0 };

    public static long MeshTicks, NormalTicks, TexBatchTicks, MeshBytes;
    public static int MeshCount, NormalCount;

    public static void Reset()
    {
        MeshTicks = NormalTicks = TexBatchTicks = MeshBytes = 0;
        MeshCount = NormalCount = 0;
    }

    public static long Mark() => Stopwatch.GetTimestamp();

    public static long Since(long t0) => Stopwatch.GetTimestamp() - t0;

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
