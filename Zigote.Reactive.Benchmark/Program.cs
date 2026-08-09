using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Zigote.Core.State;

if (args is ["selfcheck"])
{
    SignalsDotnetComparison.SelfCheck();
    return;
}

// Correctness under contention, both libraries — run this before trusting any contended throughput
// number (see ConcurrencyProbe).
if (args is ["concurrency"])
{
    ConcurrencyProbe.Run();
    return;
}

// No args → run everything (both the core suite and the SignalsDotnet head-to-head) instead of dropping
// into BenchmarkSwitcher's interactive menu.
if (args.Length == 0) args = ["--filter", "*"];
BenchmarkSwitcher.FromTypes(
        [typeof(ReactiveBenchmarks), typeof(SignalsDotnetComparison), typeof(ContentionBenchmarks)]
    )
    .Run(args);

/// <summary>Medium-run, in-process — same shape as the SignalsDotnet perf project this is ported from.</summary>
public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.MediumRun.WithToolchain(InProcessNoEmitToolchain.Instance));
    }
}

/// <summary>
///     Head-to-head job: out-of-process and short. The in-process toolchain refuses benchmarks this slow
///     (a SignalsDotnet chain recompute is orders of magnitude past a Zigote one), and both sides of every
///     pair run under the same job, which is what the comparison needs.
/// </summary>
public class ComparisonConfig : ManualConfig
{
    public ComparisonConfig()
    {
        AddJob(Job.ShortRun);
    }
}

/// <summary>
///     Reactive-core throughput and allocation, covering every shape in the signals improvement plan's
///     baseline table plus <see cref="ComputedRoundTrip" />, the direct port of SignalsDotnet's
///     <c>ComputedBenchmarks</c> (github.com/fedeAlterio/SignalsDotnet) — so both sets of numbers stay
///     comparable. Reads, writes, propagation shapes (diamond / chain / fan-out / wide), the lazy
///     unobserved path, batching, deferred-effect draining, and two-thread write contention.
///     <para>Run: <c>dotnet run -c Release --project Zigote.Reactive.Benchmark</c></para>
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class ReactiveBenchmarks
{
    private const int FanOut = 32;
    private const int WideDeps = 64;
    private const int ContendedWrites = 5_000; // per thread

    private readonly Signal<int> _bare = new(0);
    private readonly Signal<int> _batchA = new(0);
    private readonly Signal<int> _batchB = new(0);
    private readonly Action _batchBody;
    private readonly Computed<int> _chain10;
    private readonly Signal<int> _chain10Root = new(0);
    private readonly Computed<int> _chain100;
    private readonly Signal<int> _chain100Root = new(0);
    private readonly Computed<int> _computed;
    private readonly Signal<int> _contendedA = new(0);
    private readonly Signal<int> _contendedB = new(0);
    private readonly Signal<int> _deferredSource = new(0);
    private readonly Computed<int> _diamond;
    private readonly Signal<int> _diamondRoot = new(0);
    private readonly Signal<int> _effectSource = new(0);
    private readonly Computed<int>[] _fan = new Computed<int>[FanOut];
    private readonly Signal<int> _fanSource = new(0);
    private readonly Computed<int> _lazy;
    private readonly Signal<int> _lazySource = new(0);
    private readonly List<IDisposable> _roots = [];
    private readonly Signal<int> _signal = new(0);
    private readonly Computed<int> _wide;
    private readonly Signal<int>[] _wideSources = new Signal<int>[WideDeps];
    private int _batchValue;
    private int _contendedSink;
    private int _deferredSink;
    private int _effectSink;
    private int _flip;

    public ReactiveBenchmarks()
    {
        // The ported round trip: an observed computed over one signal.
        _computed = Computed.From(() => _signal.Value);
        _roots.Add(_computed.Observe(() => { }));

        _diamond = Computed.From(() =>
            {
                var left = _diamondRoot.Value + 1;
                var right = _diamondRoot.Value * 2;
                return left + right;
            }
        );
        _roots.Add(_diamond.Observe(() => { }));

        _chain10 = BuildChain(_chain10Root, 10);
        _chain100 = BuildChain(_chain100Root, 100);

        for (var i = 0; i < FanOut; i++)
        {
            var c = Computed.From(() => _fanSource.Value + 1);
            _fan[i] = c;
            _roots.Add(c.Observe(() => { }));
        }

        for (var i = 0; i < WideDeps; i++) _wideSources[i] = new Signal<int>(0);
        _wide = Computed.From(() =>
            {
                var sum = 0;
                for (var i = 0; i < WideDeps; i++) sum += _wideSources[i].Value;
                return sum;
            }
        );
        _roots.Add(_wide.Observe(() => { }));

        _roots.Add(new Effect(() => _effectSink = _effectSource.Value));

        // Deferred: a write only marks it; the body runs on the host's DrainDeferred pass.
        _roots.Add(
            new Effect(() => _deferredSink = _deferredSource.Value, EffectAffinity.Deferred)
        );

        // Two disjoint single-signal graphs, written from two threads — measures gate contention.
        _roots.Add(new Effect(() => _contendedSink += _contendedA.Value));
        _roots.Add(new Effect(() => _contendedSink += _contendedB.Value));

        // Cached so the batch measurement is the batch itself, not a closure allocation per call.
        _batchBody = () =>
        {
            _batchA.Value = _batchValue;
            _batchB.Value = _batchValue;
        };

        // Never observed → recomputes lazily on read, no push cascade.
        _lazy = Computed.From(() => _lazySource.Value + 1);
        _ = _lazy.Value; // warm, so LazyComputedReadClean measures the validated fast-out
    }

    // ── reads ────────────────────────────────────────────────────────────────────

    /// <summary>Tracked read: the graph's hottest operation (gate + eval-context lookup).</summary>
    [Benchmark]
    public int SignalRead()
    {
        return _signal.Value;
    }

    /// <summary>Untracked read — the same minus dependency registration.</summary>
    [Benchmark]
    public int SignalPeek()
    {
        return _signal.Peek();
    }

    /// <summary>Unobserved computed, nothing written since: must fast-out on the global version.</summary>
    [Benchmark]
    public int LazyComputedReadClean()
    {
        return _lazy.Value;
    }

    // ── writes and propagation ───────────────────────────────────────────────────

    /// <summary>Port of SignalsDotnet <c>ComputedRoundTrip</c>: two writes then a read of an observed computed.</summary>
    [Benchmark(Baseline = true)]
    public int ComputedRoundTrip()
    {
        _ = _computed.Value;
        _signal.Value = 0;
        _signal.Value = 1;
        return _computed.Value;
    }

    /// <summary>Floor: a write nobody observes (the common case for scratch state).</summary>
    [Benchmark]
    public int SignalWriteUnobserved()
    {
        _bare.Value = ++_flip;
        return _bare.Value;
    }

    /// <summary>One write, one effect re-run — the widget-rebuild path.</summary>
    [Benchmark]
    public int EffectRoundTrip()
    {
        _effectSource.Value = ++_flip;
        return _effectSink;
    }

    /// <summary>Deferred effect: the write only marks it, the host's drain runs the body.</summary>
    [Benchmark]
    public int DeferredEffectRoundTrip()
    {
        _deferredSource.Value = ++_flip;
        Reactive.DrainDeferred();
        return _deferredSink;
    }

    /// <summary>Diamond: one write reaching a shared node through two paths must settle it once.</summary>
    [Benchmark]
    public int DiamondRoundTrip()
    {
        _diamondRoot.Value = ++_flip;
        return _diamond.Value;
    }

    /// <summary>Chain of 10 computeds — the plan's baseline depth.</summary>
    [Benchmark]
    public int Chain10RoundTrip()
    {
        _chain10Root.Value = ++_flip;
        return _chain10.Value;
    }

    /// <summary>Chain of 100 — depth scaling check.</summary>
    [Benchmark]
    public int Chain100RoundTrip()
    {
        _chain100Root.Value = ++_flip;
        return _chain100.Value;
    }

    /// <summary>Fan-out: one signal, 32 observed computeds (a list of bound widgets).</summary>
    [Benchmark]
    public int FanOutRoundTrip()
    {
        _fanSource.Value = ++_flip;
        return _fan[FanOut - 1].Value;
    }

    /// <summary>Wide computed: 64 dependencies, one of them written (the dedupe-scan cost, plan item 7).</summary>
    [Benchmark]
    public int WideComputedRoundTrip()
    {
        _wideSources[0].Value = ++_flip;
        return _wide.Value;
    }

    /// <summary>Two writes coalesced into a single downstream pass.</summary>
    [Benchmark]
    public void BatchedWrites()
    {
        _batchValue = ++_flip;
        Reactive.Batch(_batchBody);
    }

    /// <summary>Unobserved computed after a write: no push cascade, re-derived on the next read.</summary>
    [Benchmark]
    public int LazyComputedReadAfterWrite()
    {
        _lazySource.Value = ++_flip;
        return _lazy.Value;
    }

    // ── contention ───────────────────────────────────────────────────────────────

    /// <summary>10k writes over two disjoint graphs, one thread — the baseline for the next benchmark.</summary>
    [Benchmark(OperationsPerInvoke = ContendedWrites * 2)]
    public void WritesOneThread()
    {
        for (var i = 0; i < ContendedWrites; i++) _contendedA.Value = i;
        for (var i = 0; i < ContendedWrites; i++) _contendedB.Value = i;
    }

    /// <summary>Same 10k writes, two threads on disjoint graphs — what the single global gate costs.</summary>
    [Benchmark(OperationsPerInvoke = ContendedWrites * 2)]
    public void WritesTwoThreads()
    {
        Parallel.Invoke(
            () =>
            {
                for (var i = 0; i < ContendedWrites; i++) _contendedA.Value = i;
            },
            () =>
            {
                for (var i = 0; i < ContendedWrites; i++) _contendedB.Value = i;
            }
        );
    }

    private Computed<int> BuildChain(Signal<int> root, int depth)
    {
        var node = Computed.From(() => root.Value + 1);
        for (var i = 1; i < depth; i++)
        {
            var prev = node;
            node = Computed.From(() => prev.Value + 1);
        }

        _roots.Add(node.Observe(() => { }));
        return node;
    }
}