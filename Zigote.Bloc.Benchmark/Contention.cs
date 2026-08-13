using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Zigote.Bloc;

/// <summary>
///     What the pump's single lock costs as producers are added — the bloc-level counterpart to
///     <c>Zigote.Reactive.Benchmark</c>'s <c>ContentionBenchmarks</c>, and the number that decides
///     whether a bloc can safely be fed from background work or wants a per-feature one.
///     <para>
///         <b>Total work is held constant</b> (<see cref="TotalOps" /> events however many threads
///         share
///         them) and <c>OperationsPerInvoke</c> is that same constant, so the Mean column reads
///         directly
///         as <em>nanoseconds per event</em> and rows are comparable straight down. Flat means adding
///         producers is free; rising means they are serialising.
///     </para>
///     <para>
///         The shared row is the one to watch, and not only for the lock: whichever caller wins
///         <c>_pumping</c> drains for <i>everyone</i>, so the others enqueue and leave while one
///         thread
///         does all the handling. That is the intended design — it is what keeps handlers from running
///         concurrently — but it means throughput past one producer is bounded by a single consumer,
///         and
///         this benchmark is where that ceiling becomes visible instead of surprising.
///     </para>
///     <para>
///         Run:
///         <c>dotnet run -c Release --project Zigote.Bloc.Benchmark -- --filter *Contention*</c>
///     </para>
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BlocContentionConfig))]
public class BlocContentionBenchmarks
{
    // Divisible by every Threads value below, so each producer gets exactly the same share.
    private const int TotalOps = 32_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    [Params(
        1,
        2,
        4,
        8,
        16
    )]
    public int Threads;

    private DrainCounter[] _disjoint = [];
    private ParallelOptions _options = new();
    private DrainCounter _shared = null!;

    [GlobalSetup]
    public void Setup()
    {
        // ponytail: Parallel.For rather than barrier-synced dedicated threads — the pool is warm after
        // the warmup iterations and its dispatch amortises over 32k events. Switch to a Barrier + long
        // running threads if the 1-thread row ever stops matching BlocBenchmarks.AddBurst.
        _options = new ParallelOptions { MaxDegreeOfParallelism = Threads };

        _shared = new DrainCounter();
        _disjoint = new DrainCounter[Threads];
        for (int i = 0; i < Threads; i++) _disjoint[i] = new DrainCounter();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _shared.Dispose();
        foreach (var bloc in _disjoint) bloc.Dispose();
    }

    /// <summary>
    ///     N producers, N blocs, one each — no sharing at all, so anything above flat is the cost of
    ///     the lock being taken rather than the cost of it being contended.
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOps)]
    public void DisjointBlocs()
    {
        int perThread = TotalOps / Threads;
        Parallel.For(
            fromInclusive: 0,
            toExclusive: Threads,
            parallelOptions: _options,
            body: t =>
            {
                var bloc = _disjoint[t];
                for (int i = 0; i < perThread; i++) bloc.Add(Bump.One);
            }
        );
    }

    /// <summary>
    ///     N producers, one bloc, timed until the last event has actually been handled — lock
    ///     contention plus the single-consumer ceiling.
    /// </summary>
    [Benchmark(OperationsPerInvoke = TotalOps)]
    public void SharedBloc()
    {
        int perThread = TotalOps / Threads;
        _shared.Expect(perThread * Threads);

        Parallel.For(
            fromInclusive: 0,
            toExclusive: Threads,
            parallelOptions: _options,
            body: _ =>
            {
                for (int i = 0; i < perThread; i++) _shared.Add(Bump.One);
            }
        );

        if (!_shared.AwaitExpected(Budget)) throw new TimeoutException("pump did not drain");
    }

    /// <summary>
    ///     The same shared bloc with a <see cref="BlocObserver" /> attached, because a timeline is a
    ///     process-wide hook: if observation adds contention of its own, it does so exactly here and
    ///     not in the single-threaded rows.
    /// </summary>
    [Benchmark(OperationsPerInvoke = TotalOps)]
    public void SharedBlocObserved()
    {
        int perThread = TotalOps / Threads;
        int events = 0;
        BlocObserver.OnEvent = (_, _) => Interlocked.Increment(ref events);

        try
        {
            _shared.Expect(perThread * Threads);
            Parallel.For(
                fromInclusive: 0,
                toExclusive: Threads,
                parallelOptions: _options,
                body: _ =>
                {
                    for (int i = 0; i < perThread; i++) _shared.Add(Bump.One);
                }
            );

            if (!_shared.AwaitExpected(Budget)) throw new TimeoutException("pump did not drain");
        }
        finally
        {
            BlocObserver.OnEvent = null;
        }
    }
}

/// <summary>
///     Out-of-process, because a contended invocation is far too slow for the in-process toolchain,
///     and
///     every <see cref="BlocContentionBenchmarks.Threads" /> value runs under one job so the rows stay
///     comparable.
///     <para>
///         Wider than <see cref="Job.ShortRun" />, which is what the reactive project's equivalent
///         uses:
///         at three iterations the margins here came back as much as 115% of the mean, which is not a
///         measurement — thread scheduling adds variance that a signal write on one thread does not
///         have.
///         Fifteen iterations after ten warmups puts the margins inside the effect being looked for.
///     </para>
/// </summary>
public class BlocContentionConfig : ManualConfig
{
    public BlocContentionConfig() => AddJob(
        Job.Default.WithIterationCount(15).WithWarmupCount(10).WithLaunchCount(1)
    );
}
