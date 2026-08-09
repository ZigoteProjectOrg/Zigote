using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Zigote.Bloc;
using Zigote.Core.State;

// No args → run everything rather than dropping into BenchmarkSwitcher's interactive menu, same as
// Zigote.Reactive.Benchmark.
if (args.Length == 0) args = ["--filter", "*"];
BenchmarkSwitcher.FromTypes(
        [typeof(BlocBenchmarks), typeof(DispatchComparison), typeof(BlocContentionBenchmarks)]
    )
    .Run(args);

/// <summary>Medium-run, in-process — the dispatch numbers are nanosecond-scale.</summary>
public class BlocConfig : ManualConfig
{
    public BlocConfig()
    {
        AddJob(Job.MediumRun.WithToolchain(InProcessNoEmitToolchain.Instance));
    }
}

/// <summary>
///     Out-of-process and short, for anything that parks a thread or waits on a background pump: the
///     in-process toolchain refuses invocations that slow, and both halves of a head-to-head pair must
///     run under one job for the rows to be comparable.
/// </summary>
public class BlocComparisonConfig : ManualConfig
{
    public BlocComparisonConfig()
    {
        AddJob(Job.ShortRun);
    }
}

/// <summary>
///     Dispatch cost and allocation for <see cref="Bloc{TEvent,TState}" />, one thread.
///     <para>
///         The shapes here are the ones the README makes promises about — "synchronous when the handler
///         is", "allocation-free on that synchronous path", state deduplicated by the signal, and
///         <c>Select</c> as the way a view avoids waking on a field it does not read. Each promise gets
///         a row, because a promise nobody measured is a promise that quietly stops being true.
///     </para>
///     <para>Run: <c>dotnet run -c Release --project Zigote.Bloc.Benchmark</c></para>
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BlocConfig))]
public class BlocBenchmarks
{
    private const int Burst = 1_000;

    private readonly AsyncCounter _async = new();
    private readonly SyncCounter _observed = new();
    private readonly SyncCounter _sync = new();

    // One bloc per watcher, never one bloc with both: a shared bloc would run BOTH reactions on every
    // Add, so the two rows would measure the same thing and the Select row would look no cheaper —
    // which is exactly the wrong-looking result the first run of this benchmark produced.
    private readonly SyncCounter _selectWatched = new();
    private readonly SyncCounter _stateWatched = new();

    private IDisposable? _selectLive;
    private Computed<bool>? _selected;
    private int _selectedRuns;
    private IDisposable? _stateLive;
    private int _stateRuns;

    [GlobalSetup(Target = nameof(ValueChangeWakesStateWatcher))]
    public void SetupStateWatcher()
    {
        // A Watch is an Effect over what its body read; this is that, minus the widget tree.
        _stateLive = new Effect(() =>
            {
                _ = _stateWatched.State.Value;
                _stateRuns++;
            }
        );
    }

    [GlobalSetup(Target = nameof(ValueChangeSkipsSelectWatcher))]
    public void SetupSelectWatcher()
    {
        _selected = _selectWatched.Select(s => s.Busy);
        _selectLive = new Effect(() =>
            {
                _ = _selected.Value;
                _selectedRuns++;
            }
        );
    }

    [GlobalSetup(Target = nameof(AddObserved))]
    public void AttachObserver()
    {
        BlocObserver.OnEvent = static (_, _) => { };
        BlocObserver.OnChange = static (_, _, _) => { };
    }

    [GlobalCleanup(Target = nameof(AddObserved))]
    public void DetachObserver()
    {
        BlocObserver.OnEvent = null;
        BlocObserver.OnChange = null;
    }

    [GlobalCleanup(Target = nameof(ValueChangeWakesStateWatcher))]
    public void CleanupStateWatcher()
    {
        _stateLive?.Dispose();
    }

    [GlobalCleanup(Target = nameof(ValueChangeSkipsSelectWatcher))]
    public void CleanupSelectWatcher()
    {
        _selectLive?.Dispose();
        _selected?.Dispose();
    }

    // ── dispatch ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The number everything else is relative to: one tap, through the queue, handled, state
    ///     emitted, all before <c>Add</c> returns. Allocation is the new state record and nothing else.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int AddSync()
    {
        _sync.Add(Bump.One);
        return _sync.Current.Value;
    }

    /// <summary>Dispatch floor: through the pump, handler decides to do nothing. Must allocate zero.</summary>
    [Benchmark]
    public void AddWithoutEmitting()
    {
        _sync.Add(Noop.Instance);
    }

    /// <summary>
    ///     The same work on the <c>async ValueTask</c> base, completing without ever suspending — the
    ///     cost of the async signature on the path that does not use it. The gap to
    ///     <see cref="AddSync" /> is what <see cref="SyncBloc{TEvent,TState}" /> exists to remove.
    /// </summary>
    [Benchmark]
    public int AddAsyncCompletingSynchronously()
    {
        _async.Add(Bump.One);
        return _async.Current.Value;
    }

    /// <summary>
    ///     An emit the signal deduplicates: the record is built and compared, then nothing propagates.
    ///     The delta to <see cref="AddSync" /> is what a redundant emit costs a frame.
    /// </summary>
    [Benchmark]
    public int AddDeduplicatedEmit()
    {
        _sync.Add(Bump.Zero);
        return _sync.Current.Value;
    }

    /// <summary>
    ///     One event whose handler adds another: two dispatches, the second through the queue rather
    ///     than nested. The ordering guarantee's price.
    /// </summary>
    [Benchmark]
    public int AddNested()
    {
        _sync.Add(Chained.Instance);
        return _sync.Current.Value;
    }

    /// <summary>
    ///     A burst arriving faster than a frame — one caller drains the lot, so the per-event cost
    ///     here excludes the lock round trip that <see cref="AddSync" /> pays on every call.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Burst)]
    public int AddBurst()
    {
        for (var i = 0; i < Burst; i++) _sync.Add(Bump.One);
        return _sync.Current.Value;
    }

    // ── observation ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     <see cref="AddSync" /> with both <see cref="BlocObserver" /> hooks attached — a DevTools
    ///     timeline running in a release build. The delta is the whole cost of observability: two
    ///     delegate calls and the extra <c>Peek</c> that reads the pre-emit state.
    /// </summary>
    [Benchmark]
    public int AddObserved()
    {
        _observed.Add(Bump.One);
        return _observed.Current.Value;
    }

    // ── what a view pays ─────────────────────────────────────────────────────────

    /// <summary>A watcher that read the whole state: every field's movement rebuilds it.</summary>
    [Benchmark]
    public int ValueChangeWakesStateWatcher()
    {
        _stateWatched.Add(Bump.One);
        return _stateRuns;
    }

    /// <summary>
    ///     A watcher behind <c>Select(s =&gt; s.Busy)</c> while <c>Value</c> is what moves: the
    ///     projection re-runs, its result does not change, and the watcher is never woken. This is the
    ///     "list keyed on what is playing does not rebuild at the playback clock's rate" case.
    /// </summary>
    [Benchmark]
    public int ValueChangeSkipsSelectWatcher()
    {
        _selectWatched.Add(Bump.One);
        return _selectedRuns;
    }
}

// ── the blocs under test ─────────────────────────────────────────────────────────

public abstract record CounterEvent;

/// <summary>Cached instances: a benchmark that allocated its own event would be measuring that.</summary>
public sealed record Bump(int By) : CounterEvent
{
    public static readonly Bump One = new(1);
    public static readonly Bump Zero = new(0);
}

public sealed record Noop : CounterEvent
{
    public static readonly Noop Instance = new();
}

public sealed record Chained : CounterEvent
{
    public static readonly Chained Instance = new();
}

public sealed record CounterState(int Value, bool Busy);

public sealed class SyncCounter() : SyncBloc<CounterEvent, CounterState>(new CounterState(0, false))
{
    protected override void OnEvent(CounterEvent @event)
    {
        switch (@event)
        {
            case Bump(var by):
                Emit(Current with { Value = unchecked(Current.Value + by) });
                break;
            case Chained:
                Add(Bump.One);
                break;
            case Noop:
                break;
        }
    }
}

public sealed class AsyncCounter() : Bloc<CounterEvent, CounterState>(new CounterState(0, false))
{
    protected override ValueTask OnEventAsync(CounterEvent @event, CancellationToken ct)
    {
        if (@event is Bump(var by)) Emit(Current with { Value = unchecked(Current.Value + by) });
        return default;
    }
}
