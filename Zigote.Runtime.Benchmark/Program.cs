namespace Zigote.RuntimeBench;

/// <summary>
///     Headless throughput probes for the gameplay-side hot loops named in
///     <c>docs/notes/parallelism-simd-and-atomics.md</c> Phase 2. Each probe answers one question:
///     is this loop worth restructuring, and by how much?
///     <para>
///         Two numbers decide, not one. A ratio ("SoA is 4× on that pass") is meaningless until it
///         is multiplied by the share of a real step that pass is, and then by what the subsystem
///         costs at all. Every probe prints all three, and the verdict weighs the absolute number —
///         that is what keeps this from justifying an API break for 0.2% of a core.
///     </para>
///     <para>Run: <c>dotnet run -c Release --project Zigote.Runtime.Benchmark -- [vfx|dispatch|physics2d|ui|all] [n]</c></para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (ProbeFailedException failed)
        {
            Console.Error.WriteLine($"probe failed: {failed.Message}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        string probe = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        string[] rest = args.Length > 1 ? args[1..] : [];

        switch (probe)
        {
            case "performance":
                PerformanceProbe.Run(rest);
                break;
            case "performance-window":
                WindowPerformanceProbe.Run(rest);
                break;
            case "vfx":
                VfxProbe.Run(rest);
                break;
            case "physics2d":
                Physics2DProbe.Run(rest);
                Console.WriteLine();
                UiScrollProbe.Run([]);
                break;
            case "dispatch":
                DispatchProbe.Run(rest);
                break;
            case "ui":
                UiScrollProbe.Run(rest);
                break;
            case "all":
                VfxProbe.Run(rest);
                Console.WriteLine();
                DispatchProbe.Run(rest);
                Console.WriteLine();
                Physics2DProbe.Run(rest);
                break;
            default:
                Console.Error.WriteLine($"unknown probe '{probe}' — expected vfx, dispatch, physics2d, ui, all, performance or performance-window");
                return 1;
        }

        return 0;
    }

    /// <summary>Warm past tiered JIT, then take the best of three runs — the least noisy estimator.</summary>
    internal static double Time(int steps, Action body)
    {
        for (int i = 0; i < 200; i++) body();

        double best = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < steps; i++) body();
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            best = Math.Min(val1: best, val2: ms * 1e6 / steps);
        }

        return best; // ns per step
    }
}
