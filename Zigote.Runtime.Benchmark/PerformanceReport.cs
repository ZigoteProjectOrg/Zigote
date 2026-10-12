using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Zigote.RuntimeBench;

/// <summary>Raw observations as well as quantiles; no best-of selection or forced collections.</summary>
internal sealed class PerformanceReport(string mode)
{
    public string Mode { get; } = mode;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string OS { get; } = RuntimeInformation.OSDescription;
    public string Architecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();
    public int LogicalProcessors { get; } = Environment.ProcessorCount;
    public long StopwatchFrequency { get; } = Stopwatch.Frequency;
    public bool ServerGC { get; } = System.Runtime.GCSettings.IsServerGC;
    public string TieredCompilation { get; } = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default";
    public Dictionary<string, string> Metadata { get; } = [];
    public List<Observation> Observations { get; } = [];

    public sealed record Observation(string Name, string Unit, double[] Samples,
        double P50, double P95, double P99, double Max);

    public void Add(string name, string unit, double[] samples)
    {
        if (samples.Length == 0 || samples.Any(x => !double.IsFinite(x) || x < 0))
            throw new InvalidOperationException($"Invalid measurements for {name}");
        double[] sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        double Q(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
        var observation = new Observation(name, unit, samples, Q(.50), Q(.95), Q(.99), sorted[^1]);
        Observations.Add(observation);
        Console.WriteLine($"{name,-48} {unit,-9} p50={observation.P50,10:F3} p95={observation.P95,10:F3} p99={observation.P99,10:F3} max={observation.Max,10:F3}");
    }

    // Setup is excluded from both counters. The supplied action must do the observable work;
    // callers validate its result separately. The array and delegate allocations are excluded.
    public void Measure(string name, int warmup, int samples, Action action, Action? setup = null)
    {
        for (int i = 0; i < warmup; i++) { setup?.Invoke(); action(); }
        var times = new double[samples];
        var allocations = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            setup?.Invoke();
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            action();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Add(name, "ms", times);
        Add(name + ".ui_alloc", "bytes", allocations);
    }

    public void Save(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Supply one output JSON path.");
        string path = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Raw measurements: {path}");
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new ProbeFailedException(message);
    }

    /// <summary>Injected callback service time, not an estimate of any application callback.</summary>
    public static void BusyFor(double milliseconds)
    {
        long end = Stopwatch.GetTimestamp() + (long)(milliseconds * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(4);
    }
}

/// <summary>
///     A probe's workload check did not hold. Caught in <c>Main</c> and turned into a message and a
///     nonzero exit code: left unhandled it aborts the process, which the OS files as a crash.
/// </summary>
internal sealed class ProbeFailedException(string message) : Exception(message);
