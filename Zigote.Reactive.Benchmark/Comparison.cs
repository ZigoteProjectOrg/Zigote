using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using R3;
using SdEffect = SignalsDotnet.Effect;
using SdSignal = SignalsDotnet.Signal<int>;
using SdSignals = SignalsDotnet.Signal;
using SdReadOnly = SignalsDotnet.IReadOnlySignal<int>;
using ZComputed = Zigote.Core.State.Computed<int>;
using ZComputeds = Zigote.Core.State.Computed;
using ZEffect = Zigote.Core.State.Effect;
using ZExt = Zigote.Core.State.ReactiveExtensions;
using ZReactive = Zigote.Core.State.Reactive;
using ZSignal = Zigote.Core.State.Signal<int>;

/// <summary>
///     Head-to-head against SignalsDotnet (github.com/fedeAlterio/SignalsDotnet, the Angular-Signals port
///     built on R3), the closest .NET equivalent of this core. Same graph shape, same machine, same job —
///     each pair is one category, with Zigote as that category's baseline, so the Ratio column reads
///     directly as "how many times the Zigote cost". <c>ComputedRoundTrip</c> is their own benchmark,
///     ported verbatim on both sides.
///     <para>
///         Fairness notes: every SignalsDotnet computed is kept live by a subscription (their default
///         configuration is already <c>SubscribeWeakly = false</c>, so nothing is collected mid-run),
///         matching a Zigote computed made watched by <c>Observe</c> — neither side is measured in its
///         lazy-unobserved state. Their <c>Effect.AtomicOperation</c> is the counterpart of
///         <c>Reactive.Batch</c>.
///     </para>
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Config(typeof(ComparisonConfig))]
public class SignalsDotnetComparison
{
    private const int FanOut = 32;
    private const int ChainDepth = 10;
    private const int ContendedThreads = 8;
    private const int ContendedOps = 16_000; // divisible by ContendedThreads

    private static readonly ParallelOptions ParallelOptions =
        new() { MaxDegreeOfParallelism = ContendedThreads };

    private readonly List<IDisposable> _roots = [];

    // ── Zigote graph ─────────────────────────────────────────────────────────────
    private readonly ZSignal _zSignal = new(0);
    private readonly ZComputed _zComputed;
    private readonly ZSignal _zEffectSource = new(0);
    private readonly ZSignal _zDiamondRoot = new(0);
    private readonly ZComputed _zDiamond;
    private readonly ZSignal _zChainRoot = new(0);
    private readonly ZComputed _zChain;
    private readonly ZSignal _zFanSource = new(0);
    private readonly ZComputed[] _zFan = new ZComputed[FanOut];
    private readonly ZSignal _zContended = new(0);
    private readonly ZSignal _zBatchA = new(0);
    private readonly ZSignal _zBatchB = new(0);
    private readonly Action _zBatchBody;
    private int _zEffectSink;

    // ── SignalsDotnet graph ──────────────────────────────────────────────────────
    private readonly SdSignal _sdSignal = new(0);
    private readonly SdReadOnly _sdComputed;
    private readonly SdSignal _sdEffectSource = new(0);
    private readonly SdSignal _sdDiamondRoot = new(0);
    private readonly SdReadOnly _sdDiamond;
    private readonly SdSignal _sdChainRoot = new(0);
    private readonly SdReadOnly _sdChain;
    private readonly SdSignal _sdFanSource = new(0);
    private readonly SdReadOnly[] _sdFan = new SdReadOnly[FanOut];
    private readonly SdSignal _sdContended = new(0);
    private readonly SdSignal _sdBatchA = new(0);
    private readonly SdSignal _sdBatchB = new(0);
    private readonly Action _sdBatchBody;
    private int _sdEffectSink;

    private int _batchValue;
    private int _flip;

    public SignalsDotnetComparison()
    {
        // ── Zigote ──
        _zComputed = ZComputeds.From(() => _zSignal.Value);
        _roots.Add(ZExt.Observe(_zComputed, () => { }));

        _zDiamond = ZComputeds.From(() =>
            {
                var left = _zDiamondRoot.Value + 1;
                var right = _zDiamondRoot.Value * 2;
                return left + right;
            }
        );
        _roots.Add(ZExt.Observe(_zDiamond, () => { }));

        var zNode = ZComputeds.From(() => _zChainRoot.Value + 1);
        for (var i = 1; i < ChainDepth; i++)
        {
            var prev = zNode;
            zNode = ZComputeds.From(() => prev.Value + 1);
        }

        _zChain = zNode;
        _roots.Add(ZExt.Observe(_zChain, () => { }));

        for (var i = 0; i < FanOut; i++)
        {
            _zFan[i] = ZComputeds.From(() => _zFanSource.Value + 1);
            _roots.Add(ZExt.Observe(_zFan[i], () => { }));
        }

        _roots.Add(new ZEffect(() => _zEffectSink = _zEffectSource.Value));

        // Contended pair: a live computed on each side, so a write actually cascades.
        var zc = ZComputeds.From(() => _zContended.Value + 1);
        _roots.Add(ZExt.Observe(zc, () => { }));
        _zBatchBody = () =>
        {
            _zBatchA.Value = _batchValue;
            _zBatchB.Value = _batchValue;
        };

        // ── SignalsDotnet ──
        _sdComputed = Live(SdSignals.Computed(() => _sdSignal.Value));

        _sdDiamond = Live(
            SdSignals.Computed(() =>
                {
                    var left = _sdDiamondRoot.Value + 1;
                    var right = _sdDiamondRoot.Value * 2;
                    return left + right;
                }
            )
        );

        var sdNode = SdSignals.Computed(() => _sdChainRoot.Value + 1);
        for (var i = 1; i < ChainDepth; i++)
        {
            var prev = sdNode;
            sdNode = SdSignals.Computed(() => prev.Value + 1);
        }

        _sdChain = Live(sdNode);

        for (var i = 0; i < FanOut; i++)
            _sdFan[i] = Live(SdSignals.Computed(() => _sdFanSource.Value + 1));

        _roots.Add(new SdEffect(() => _sdEffectSink = _sdEffectSource.Value));
        Live(SdSignals.Computed(() => _sdContended.Value + 1));
        _sdBatchBody = () =>
        {
            _sdBatchA.Value = _batchValue;
            _sdBatchB.Value = _batchValue;
        };
    }

    // ── tracked read ─────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Read")]
    public int Zigote_Read()
    {
        return _zSignal.Value;
    }

    [Benchmark]
    [BenchmarkCategory("Read")]
    public int SignalsDotnet_Read()
    {
        return _sdSignal.Value;
    }

    // ── their own benchmark: write, write, read an observed computed ─────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ComputedRoundTrip")]
    public int Zigote_ComputedRoundTrip()
    {
        _ = _zComputed.Value;
        _zSignal.Value = 0;
        _zSignal.Value = 1;
        return _zComputed.Value;
    }

    [Benchmark]
    [BenchmarkCategory("ComputedRoundTrip")]
    public int SignalsDotnet_ComputedRoundTrip()
    {
        _ = _sdComputed.Value;
        _sdSignal.Value = 0;
        _sdSignal.Value = 1;
        return _sdComputed.Value;
    }

    // ── one write, one effect re-run ─────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Effect")]
    public int Zigote_EffectRoundTrip()
    {
        _zEffectSource.Value = ++_flip;
        return _zEffectSink;
    }

    [Benchmark]
    [BenchmarkCategory("Effect")]
    public int SignalsDotnet_EffectRoundTrip()
    {
        _sdEffectSource.Value = ++_flip;
        return _sdEffectSink;
    }

    // ── diamond: one write reaching a node through two paths ─────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Diamond")]
    public int Zigote_Diamond()
    {
        _zDiamondRoot.Value = ++_flip;
        return _zDiamond.Value;
    }

    [Benchmark]
    [BenchmarkCategory("Diamond")]
    public int SignalsDotnet_Diamond()
    {
        _sdDiamondRoot.Value = ++_flip;
        return _sdDiamond.Value;
    }

    // ── chain of 10 computeds ────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Chain10")]
    public int Zigote_Chain10()
    {
        _zChainRoot.Value = ++_flip;
        return _zChain.Value;
    }

    [Benchmark]
    [BenchmarkCategory("Chain10")]
    public int SignalsDotnet_Chain10()
    {
        _sdChainRoot.Value = ++_flip;
        return _sdChain.Value;
    }

    // ── fan-out: one signal, 32 live computeds ───────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FanOut32")]
    public int Zigote_FanOut32()
    {
        _zFanSource.Value = ++_flip;
        return _zFan[FanOut - 1].Value;
    }

    [Benchmark]
    [BenchmarkCategory("FanOut32")]
    public int SignalsDotnet_FanOut32()
    {
        _sdFanSource.Value = ++_flip;
        return _sdFan[FanOut - 1].Value;
    }

    // ── two writes coalesced into one downstream pass ────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Batch")]
    public void Zigote_Batch()
    {
        _batchValue = ++_flip;
        ZReactive.Batch(_zBatchBody);
    }

    [Benchmark]
    [BenchmarkCategory("Batch")]
    public void SignalsDotnet_Batch()
    {
        _batchValue = ++_flip;
        SdEffect.AtomicOperation(_sdBatchBody);
    }

    // ── contended: the same work split across 8 threads ──────────────────────────
    //
    // Total work is fixed at ContendedOps however many threads share it, and OperationsPerInvoke is that
    // same constant, so Mean reads as nanoseconds per operation and is directly comparable to the
    // single-threaded rows above. Read `ConcurrencyProbe` first: SignalsDotnet has no graph lock, so its
    // numbers here are what UNSYNCHRONISED access costs, not what safe access costs — the probe shows it
    // tearing struct reads that Zigote never tears. Fastest is not the same as correct.

    [Benchmark(Baseline = true, OperationsPerInvoke = ContendedOps)]
    [BenchmarkCategory("ContendedWrites")]
    public void Zigote_ContendedWrites()
    {
        Fan(t =>
            {
                var offset = t << 20;
                for (var i = 0; i < ContendedOps / ContendedThreads; i++)
                    _zContended.Value = offset | i;
            }
        );
    }

    [Benchmark(OperationsPerInvoke = ContendedOps)]
    [BenchmarkCategory("ContendedWrites")]
    public void SignalsDotnet_ContendedWrites()
    {
        Fan(t =>
            {
                var offset = t << 20;
                for (var i = 0; i < ContendedOps / ContendedThreads; i++)
                    _sdContended.Value = offset | i;
            }
        );
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ContendedOps)]
    [BenchmarkCategory("ContendedReads")]
    public int Zigote_ContendedReads()
    {
        var sink = 0;
        Fan(_ =>
            {
                var local = 0;
                for (var i = 0; i < ContendedOps / ContendedThreads; i++) local += _zContended.Value;
                Interlocked.Add(ref sink, local);
            }
        );
        return sink;
    }

    [Benchmark(OperationsPerInvoke = ContendedOps)]
    [BenchmarkCategory("ContendedReads")]
    public int SignalsDotnet_ContendedReads()
    {
        var sink = 0;
        Fan(_ =>
            {
                var local = 0;
                for (var i = 0; i < ContendedOps / ContendedThreads; i++) local += _sdContended.Value;
                Interlocked.Add(ref sink, local);
            }
        );
        return sink;
    }

    // ponytail: Parallel.For, not barrier-synced dedicated threads — the pool is warm after warmup and
    // its dispatch amortises to ~1ns over 16k ops. Swap in a Barrier if the two sides ever disagree on
    // thread count mid-run.
    private void Fan(Action<int> body)
    {
        Parallel.For(0, ContendedThreads, ParallelOptions, body);
    }

    /// <summary>Keep a SignalsDotnet computed live (the counterpart of an observed Zigote computed).</summary>
    private SdReadOnly Live(SdReadOnly computed)
    {
        _roots.Add(computed.Values.Subscribe(static _ => { }));
        return computed;
    }

    /// <summary>
    ///     Guards the comparison from measuring nothing: both graphs must actually propagate (an effect
    ///     sink that never moves, or a computed that never recomputes, would make one side look fast).
    ///     Run with <c>dotnet run -c Release --project Zigote.Reactive.Benchmark -- selfcheck</c>.
    /// </summary>
    public static void SelfCheck()
    {
        var b = new SignalsDotnetComparison();
        Check("Read", b.Zigote_Read(), b.SignalsDotnet_Read());
        Check(
            "ComputedRoundTrip",
            b.Zigote_ComputedRoundTrip(),
            b.SignalsDotnet_ComputedRoundTrip()
        );

        // The rest write ++_flip, so both sides must start from the same counter to write the same value.
        Check("Effect", Same(b.Zigote_EffectRoundTrip), Same(b.SignalsDotnet_EffectRoundTrip));
        Check("Diamond", Same(b.Zigote_Diamond), Same(b.SignalsDotnet_Diamond));
        Check("Chain10", Same(b.Zigote_Chain10), Same(b.SignalsDotnet_Chain10));
        Check("FanOut32", Same(b.Zigote_FanOut32), Same(b.SignalsDotnet_FanOut32));

        // The effect sinks must have moved off their initial 0 on both sides.
        Same(b.Zigote_EffectRoundTrip);
        Same(b.SignalsDotnet_EffectRoundTrip);
        if (b._zEffectSink == 0 || b._sdEffectSink == 0)
            throw new InvalidOperationException(
                $"effect sink never moved (zigote={b._zEffectSink}, signalsdotnet={b._sdEffectSink})"
            );

        b.Zigote_Batch();
        b.SignalsDotnet_Batch();
        Console.WriteLine("selfcheck: both graphs propagate.");
        return;

        int Same(Func<int> op)
        {
            b._flip = 0;
            return op();
        }

        static void Check(string name, int zigote, int signalsDotnet)
        {
            Console.WriteLine($"  {name,-20} zigote={zigote,-8} signalsdotnet={signalsDotnet}");
            if (zigote != signalsDotnet)
                throw new InvalidOperationException(
                    $"{name}: the two graphs disagree (zigote={zigote}, signalsdotnet={signalsDotnet}) — " +
                    "the benchmark pair is not measuring the same thing."
                );
        }
    }
}
