using System.Reflection;
using R3;
using Zigote.Core.State;
using SdSignals = SignalsDotnet.Signal;
using ZComputeds = Zigote.Core.State.Computed;
using ZExt = Zigote.Core.State.ReactiveExtensions;

/// <summary>
///     Correctness under concurrency, head to head. The benchmark pairs in
///     <see cref="SignalsDotnetComparison" /> are single-threaded; this asks the prior question — does
///     the other graph survive being written from several threads at all, and what does "survive" mean
///     (no crash? no lost updates? no torn reads?). A throughput number for a graph that corrupts
///     under
///     contention would be meaningless, so this runs first.
///     <para>
///         Every probe is reported, never asserted: the point is to record what each library actually
///         does, not to fail a build over a guarantee SignalsDotnet never claimed to offer.
///     </para>
///     <para>
///         Run: <c>dotnet run -c Release --project Zigote.Reactive.Benchmark -- concurrency</c>
///     </para>
/// </summary>
public static class ConcurrencyProbe
{
    private const int Threads = 8;
    private const int PerThread = 50_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    public static void Run()
    {
        Console.WriteLine(
            $"Concurrency probe — {Threads} threads x {PerThread} ops, " +
            $"{Environment.ProcessorCount} logical cores\n"
        );

        NaiveReadModifyWrite();
        AtomicReadModifyWrite();
        TornStructReads();
        WriteStormWithLiveComputed();
        LifecycleChurnUnderWrites();
    }

    /// <summary>
    ///     `s.Value = s.Value + 1` on both. Two separate operations, so BOTH libraries must lose
    ///     increments — included so the next probe isn't mistaken for magic.
    /// </summary>
    private static void NaiveReadModifyWrite()
    {
        int expected = Threads * PerThread;

        var z = new Signal<int>(0);
        var zErr = Run(() =>
            {
                for (int i = 0; i < PerThread; i++) z.Value = z.Value + 1;
            }
        );

        var sd = new SignalsDotnet.Signal<int>(0);
        var sdErr = Run(() =>
            {
                for (int i = 0; i < PerThread; i++) sd.Value = sd.Value + 1;
            }
        );

        Console.WriteLine("[naive read-modify-write] both are expected to lose increments");
        Report(
            label: "  zigote       ",
            actual: z.Value,
            expected: expected,
            errors: zErr
        );
        Report(
            label: "  signalsdotnet",
            actual: sd.Value,
            expected: expected,
            errors: sdErr
        );
        Console.WriteLine();
    }

    /// <summary>
    ///     The same job done atomically. Zigote has <c>Update</c> (read-modify-write under the graph
    ///     gate). Whether SignalsDotnet exposes any equivalent is the actual question, so it is resolved
    ///     by reflection rather than assumed.
    /// </summary>
    private static void AtomicReadModifyWrite()
    {
        int expected = Threads * PerThread;

        var z = new Signal<int>(0);
        var zErr = Run(() =>
            {
                for (int i = 0; i < PerThread; i++) z.Update(v => v + 1);
            }
        );

        Console.WriteLine("[atomic read-modify-write]");
        Report(
            label: "  zigote       ",
            actual: z.Value,
            expected: expected,
            errors: zErr,
            via: "Signal<T>.Update"
        );

        string[] candidates = typeof(SignalsDotnet.Signal<int>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n =>
                n.Contains(value: "Update", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                n.Contains(value: "Mutate", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                n.Contains(value: "Modify", comparisonType: StringComparison.OrdinalIgnoreCase)
            )
            .Distinct()
            .ToArray();

        Console.WriteLine(
            candidates.Length == 0
                ? "  signalsdotnet  no atomic read-modify-write primitive on Signal<T> " +
                  $"(public instance methods: {string.Join(separator: ", ", values: PublicApi())})"
                : $"  signalsdotnet  candidates: {string.Join(separator: ", ", value: candidates)}"
        );
        Console.WriteLine();
    }

    /// <summary>A 16-byte struct write is not atomic without a lock — a reader may see A != B.</summary>
    private static void TornStructReads()
    {
        Console.WriteLine("[torn reads of a 16-byte struct under a concurrent writer]");
        Console.WriteLine($"  zigote         torn={WriteZigotePairs()}");
        Console.WriteLine($"  signalsdotnet  torn={WriteSignalsDotnetPairs()}");
        Console.WriteLine();
    }

    /// <summary>Many writers against one signal with a live computed — does the cascade survive?</summary>
    private static void WriteStormWithLiveComputed()
    {
        var z = new Signal<int>(0);
        using var zc = ZComputeds.From(() => z.Value * 2);
        using var zLive = ZExt.Observe(source: zc, onChanged: () => { });
        var zErr = Run(t =>
            {
                for (int i = 0; i < PerThread; i++) z.Value = (t << 20) | i;
            }
        );

        var sd = new SignalsDotnet.Signal<int>(0);
        var sdc = SdSignals.Computed(() => sd.Value * 2);
        using var sdLive = sdc.Values.Subscribe(static _ => { });
        var sdErr = Run(t =>
            {
                for (int i = 0; i < PerThread; i++) sd.Value = (t << 20) | i;
            }
        );

        Console.WriteLine(
            "[write storm, one live computed] final computed must equal 2 x final source"
        );
        Console.WriteLine(
            $"  zigote         consistent={zc.Value == z.Value * 2} errors={Describe(zErr)}"
        );
        Console.WriteLine(
            $"  signalsdotnet  consistent={sdc.Value == sd.Value * 2} errors={Describe(sdErr)}"
        );
        Console.WriteLine();
    }

    /// <summary>Derived-node create/subscribe/dispose churn while another thread writes the source.</summary>
    private static void LifecycleChurnUnderWrites()
    {
        var z = new Signal<int>(0);
        bool zStop = false;
        var zWriter = Spin(body: () => z.Value++, stopped: () => Volatile.Read(ref zStop));
        var zErr = Run(() =>
            {
                for (int i = 0; i < 5_000; i++)
                {
                    var c = ZComputeds.From(() => z.Value + 1);
                    var obs = ZExt.Observe(source: c, onChanged: () => { });
                    _ = c.Value;
                    obs.Dispose();
                    c.Dispose();
                }
            }
        );
        Volatile.Write(location: ref zStop, value: true);
        zWriter.Wait(Budget);

        var sd = new SignalsDotnet.Signal<int>(0);
        bool sdStop = false;
        var sdWriter = Spin(body: () => sd.Value++, stopped: () => Volatile.Read(ref sdStop));
        var sdErr = Run(() =>
            {
                for (int i = 0; i < 5_000; i++)
                {
                    var c = SdSignals.Computed(() => sd.Value + 1);
                    var sub = c.Values.Subscribe(static _ => { });
                    _ = c.Value;
                    sub.Dispose();
                }
            }
        );
        Volatile.Write(location: ref sdStop, value: true);
        sdWriter.Wait(Budget);

        Console.WriteLine("[derived-node lifecycle churn under a concurrent writer]");
        Console.WriteLine($"  zigote         errors={Describe(zErr)}");
        Console.WriteLine($"  signalsdotnet  errors={Describe(sdErr)}");
        Console.WriteLine();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int WriteZigotePairs()
    {
        var s = new Signal<Pair>(new Pair(A: 0, B: 0));
        int torn = 0;
        bool stop = false;
        var readers = StartReaders(
            read: () =>
            {
                var p = s.Value;
                if (p.A != p.B) Interlocked.Increment(ref torn);
            },
            stopped: () => Volatile.Read(ref stop)
        );
        for (long i = 1; i <= 300_000; i++) s.Value = new Pair(A: i, B: i);
        Volatile.Write(location: ref stop, value: true);
        Task.WaitAll(tasks: readers.ToArray(), timeout: Budget);
        return torn;
    }

    private static int WriteSignalsDotnetPairs()
    {
        var s = new SignalsDotnet.Signal<Pair>(new Pair(A: 0, B: 0));
        int torn = 0;
        bool stop = false;
        var readers = StartReaders(
            read: () =>
            {
                var p = s.Value;
                if (p.A != p.B) Interlocked.Increment(ref torn);
            },
            stopped: () => Volatile.Read(ref stop)
        );
        for (long i = 1; i <= 300_000; i++) s.Value = new Pair(A: i, B: i);
        Volatile.Write(location: ref stop, value: true);
        Task.WaitAll(tasks: readers.ToArray(), timeout: Budget);
        return torn;
    }

    private static List<Task> StartReaders(Action read, Func<bool> stopped)
    {
        var tasks = new List<Task>();
        for (int r = 0; r < Threads / 2; r++)
        {
            tasks.Add(
                Task.Factory.StartNew(
                    action: () =>
                    {
                        while (!stopped()) read();
                    },
                    creationOptions: TaskCreationOptions.LongRunning
                )
            );
        }

        return tasks;
    }

    private static Task Spin(Action body, Func<bool> stopped)
    {
        return Task.Factory.StartNew(
            action: () =>
            {
                while (!stopped())
                {
                    try
                    {
                        body();
                    }
                    catch
                    {
                        // The writer's own failures are not this probe's subject.
                    }
                }
            },
            creationOptions: TaskCreationOptions.LongRunning
        );
    }

    private static List<Exception> Run(Action body) => Run(_ => body());

    private static List<Exception> Run(Action<int> body)
    {
        var errors = new List<Exception>();
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int id = t;
            tasks[t] = Task.Factory.StartNew(
                action: () => body(id),
                creationOptions: TaskCreationOptions.LongRunning
            );
        }

        if (!Task.WaitAll(tasks: tasks, timeout: Budget))
        {
            errors.Add(
                new TimeoutException($"did not finish within {Budget.TotalSeconds}s (deadlock?)")
            );
            return errors;
        }

        foreach (var t in tasks)
        {
            if (t.Exception is { } ex)
                errors.AddRange(ex.Flatten().InnerExceptions);
        }

        return errors;
    }

    private static void Report(
        string label,
        long actual,
        long expected,
        List<Exception> errors,
        string? via = null
    )
    {
        long lost = expected - actual;
        double pct = expected == 0 ? 0 : lost * 100.0 / expected;
        Console.WriteLine(
            $"{label}  final={actual,-10} expected={expected,-10} lost={lost,-10} ({pct:F1}%)" +
            $"  errors={Describe(errors)}{(via is null ? "" : $"  via {via}")}"
        );
    }

    private static string Describe(List<Exception> errors)
    {
        if (errors.Count == 0) return "none";
        var kinds = errors.Select(e => e.GetType().Name).Distinct().Take(3);
        return $"{errors.Count} ({string.Join(separator: ", ", values: kinds)})";
    }

    private static IEnumerable<string> PublicApi()
    {
        return typeof(SignalsDotnet.Signal<int>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Concat(
                typeof(SignalsDotnet.Signal<int>)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name)
            )
            .Distinct()
            .OrderBy(keySelector: n => n, comparer: StringComparer.Ordinal);
    }

    public readonly record struct Pair(long A, long B);
}
