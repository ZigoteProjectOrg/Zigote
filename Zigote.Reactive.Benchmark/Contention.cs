using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Zigote.Core.State;

/// <summary>
///     What the single global graph gate costs as threads are added. <see cref="ReactiveBenchmarks" />
///     answers this for one fixed pair (<c>WritesOneThread</c> / <c>WritesTwoThreads</c>); this sweeps
///     the thread count so the shape of the curve is visible, which is the number that decides whether
///     per-graph gates are worth building.
///     <para>
///         <b>Total work is held constant</b> (<see cref="TotalOps" /> operations however many threads
///         share them), and <c>OperationsPerInvoke</c> is that same constant — so the Mean column reads
///         directly as <em>nanoseconds per operation</em> and is comparable straight down a benchmark's
///         rows. Flat means the gate is free; rising means threads are serialising on it.
///     </para>
///     <para>
///         Three shapes, because they stress different halves of the lock: disjoint graphs (no logical
///         sharing at all — anything above flat here is pure gate cost), one shared written signal (the
///         worst case: gate contention plus a real cascade), and one shared read signal (the case that
///         actually dominates a frame, since a UI reads far more than it writes).
///     </para>
///     <para>Run: <c>dotnet run -c Release --project Zigote.Reactive.Benchmark -- --filter *Contention*</c></para>
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ContentionConfig))]
public class ContentionBenchmarks
{
    // Divisible by every Threads value below, so each worker gets exactly the same share.
    private const int TotalOps = 32_000;

    private readonly Signal<int> _sharedRead = new(0);
    private readonly Signal<int> _sharedWrite = new(0);
    private Computed<int>? _sharedDerived;
    private IDisposable? _sharedLive;

    private Signal<int>[] _disjoint = [];
    private Effect[] _disjointEffects = [];
    private int[] _disjointSinks = [];

    private ParallelOptions _options = new();

    [Params(1, 2, 4, 8, 16)]
    public int Threads;

    [GlobalSetup]
    public void Setup()
    {
        // ponytail: Parallel.For, not persistent barrier-synced threads — the pool is warm after the
        // warmup iterations and its ~10-20us dispatch amortises to ~1ns over 32k ops. Switch to a
        // Barrier + dedicated threads if the 1-thread row ever stops matching ReactiveBenchmarks.
        _options = new ParallelOptions { MaxDegreeOfParallelism = Threads };

        _disjoint = new Signal<int>[Threads];
        _disjointEffects = new Effect[Threads];
        _disjointSinks = new int[Threads];
        for (var i = 0; i < Threads; i++)
        {
            var slot = i;
            _disjoint[i] = new Signal<int>(0);
            // One observed reaction per graph, so a write actually cascades instead of hitting the
            // no-observers fast-out in NotifyWrite.
            _disjointEffects[i] = new Effect(() => _disjointSinks[slot] = _disjoint[slot].Value);
        }

        _sharedDerived = Computed.From(() => _sharedWrite.Value + 1);
        _sharedLive = ((ISignal)_sharedDerived).Observe(() => { });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var e in _disjointEffects) e.Dispose();
        _sharedLive?.Dispose();
        _sharedDerived?.Dispose();
    }

    /// <summary>N threads, N independent graphs. Any rise here is the gate and nothing else.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOps)]
    public void DisjointGraphWrites()
    {
        var perThread = TotalOps / Threads;
        Parallel.For(
            0,
            Threads,
            _options,
            t =>
            {
                var s = _disjoint[t];
                for (var i = 0; i < perThread; i++) s.Value = i;
            }
        );
    }

    /// <summary>N threads writing ONE signal with a live computed — gate plus a contended cascade.</summary>
    [Benchmark(OperationsPerInvoke = TotalOps)]
    public void SharedSignalWrites()
    {
        var perThread = TotalOps / Threads;
        Parallel.For(
            0,
            Threads,
            _options,
            t =>
            {
                var offset = t << 20;
                for (var i = 0; i < perThread; i++) _sharedWrite.Value = offset | i;
            }
        );
    }

    /// <summary>N threads reading ONE signal — the frame-loop-dominant case.</summary>
    [Benchmark(OperationsPerInvoke = TotalOps)]
    public int SharedSignalReads()
    {
        var perThread = TotalOps / Threads;
        var sink = 0;
        Parallel.For(
            0,
            Threads,
            _options,
            _ =>
            {
                var local = 0;
                for (var i = 0; i < perThread; i++) local += _sharedRead.Value;
                Interlocked.Add(ref sink, local);
            }
        );
        return sink;
    }
}

/// <summary>
///     Out-of-process and short: a contended invocation is far too slow for the in-process toolchain
///     (same reason <see cref="ComparisonConfig" /> exists), and every <c>Threads</c> value must run
///     under one job for the rows to be comparable.
/// </summary>
public class ContentionConfig : ManualConfig
{
    public ContentionConfig()
    {
        AddJob(Job.ShortRun);
    }
}
