using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Zigote.Bloc;
using Zigote.Core.State;

/// <summary>
///     The claim this project exists to put a number on: dispatching an event <em>inline</em> beats
///     dispatching it through a queue that a scheduler drains later — the argument
///     <c>bloc_signals</c> makes against Dart's stream-and-microtask bloc, and the reason
///     <see cref="Bloc{TEvent}.Add" /> runs the handler on the caller's thread.
///     <para>
///         Both sides do identical work: take an event, build a new state record, write it to a
///         <see cref="Signal{T}" /> through <see cref="Reactive.Sync" />. The only difference is who
///         runs the handler and when. The baseline is <see cref="Channel{T}" /> rather than a package
///         —
///         it is the in-box shape of "producer writes, a reader loop consumes", which is what a
///         stream-based pump is once the Rx vocabulary is stripped off, and a baseline that needed a
///         dependency would be measuring that dependency.
///     </para>
///     <para>
///         <b>Read the rows as "event accepted → state actually readable".</b> The channel rows
///         include
///         a wait because that is where the work finishes; the Zigote rows include no synchronisation
///         because there is nothing to wait for — by the time <c>Add</c> returns, the state is
///         written.
///         That asymmetry is the measurement, not a thumb on the scale.
///     </para>
///     <para>
///         Run:
///         <c>dotnet run -c Release --project Zigote.Bloc.Benchmark -- --filter *DispatchComparison*</c>
///     </para>
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BlocComparisonConfig))]
public class DispatchComparison
{
    private const int Burst = 10_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private ChannelCounter _channel = null!;
    private DrainCounter _zigote = null!;

    [GlobalSetup]
    public void Setup()
    {
        _zigote = new DrainCounter();
        _channel = new ChannelCounter();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _zigote.Dispose();
        _channel.Dispose();
    }

    /// <summary>A frame's worth of events, handled inline as they are added.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Burst)]
    public int ZigoteBurst()
    {
        for (int i = 0; i < Burst; i++) _zigote.Add(Bump.One);
        return _zigote.Current.Value; // already true — Add ran the handler
    }

    /// <summary>The same events through a channel, waiting for the reader loop to catch up.</summary>
    [Benchmark(OperationsPerInvoke = Burst)]
    public int ChannelBurst()
    {
        _channel.Expect(Burst);
        for (int i = 0; i < Burst; i++) _channel.Add(Bump.One);
        if (!_channel.AwaitExpected(Budget)) throw new TimeoutException("channel pump stalled");
        return _channel.Current.Value;
    }

    /// <summary>
    ///     One event, alone — the tap latency. Zigote's is a method call; the channel's is a thread
    ///     handoff, so this row is noisy by nature and its <i>magnitude</i> is the point, not its
    ///     third significant figure.
    /// </summary>
    [Benchmark]
    public int ZigoteSingleRoundTrip()
    {
        _zigote.Add(Bump.One);
        return _zigote.Current.Value;
    }

    /// <inheritdoc cref="ZigoteSingleRoundTrip" />
    [Benchmark]
    public int ChannelSingleRoundTrip()
    {
        _channel.Expect(1);
        _channel.Add(Bump.One);
        if (!_channel.AwaitExpected(Budget)) throw new TimeoutException("channel pump stalled");
        return _channel.Current.Value;
    }
}

/// <summary>
///     A <see cref="SyncBloc{TEvent,TState}" /> that can be asked "tell me when you have handled the
///     next N events". Needed wherever the adding thread is not the one that ends up doing the work —
///     under contention only one caller wins the pump and the rest enqueue and leave, so a benchmark
///     that stopped timing at the last <c>Add</c> would be timing the queue, not the handling.
/// </summary>
public sealed class DrainCounter()
    : SyncBloc<CounterEvent, CounterState>(new CounterState(Value: 0, Busy: false)),
        IDisposable
{
    private readonly ManualResetEventSlim _reached = new();
    private int _handled;
    private int _target = int.MaxValue;

    /// <summary>Arm before the adds, never during them.</summary>
    public void Expect(int count)
    {
        _reached.Reset();
        Volatile.Write(location: ref _target, value: Volatile.Read(ref _handled) + count);
    }

    public bool AwaitExpected(TimeSpan budget) => _reached.Wait(budget);

    protected override void OnEvent(CounterEvent @event)
    {
        if (@event is Bump(var by)) Emit(Current with { Value = unchecked(Current.Value + by) });
        if (Interlocked.Increment(ref _handled) >= Volatile.Read(ref _target)) _reached.Set();
    }

    protected override void OnDispose() => _reached.Dispose();
}

/// <summary>
///     The baseline pump: an unbounded channel and one reader loop. Same handler body, same signal
///     write — the event just reaches it through a scheduler instead of a call.
///     <para>
///         <c>AllowSynchronousContinuations</c> stays off deliberately. Turning it on lets the
///         writer's
///         thread run the reader's continuation, which is a way of half-becoming the inline dispatch
///         this is the baseline for.
///     </para>
/// </summary>
public sealed class ChannelCounter : IDisposable
{
    private readonly Channel<CounterEvent> _events = Channel.CreateUnbounded<CounterEvent>(
        new UnboundedChannelOptions {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        }
    );

    private readonly Task _pump;
    private readonly ManualResetEventSlim _reached = new();
    private readonly Signal<CounterState> _state = new(new CounterState(Value: 0, Busy: false));
    private int _handled;
    private int _target = int.MaxValue;

    public ChannelCounter() => _pump = Task.Run(PumpAsync);

    public CounterState Current => _state.Peek();

    public void Dispose()
    {
        _events.Writer.TryComplete();
        _pump.Wait(TimeSpan.FromSeconds(30));
        _reached.Dispose();
    }

    public void Add(CounterEvent @event) => _events.Writer.TryWrite(@event);

    /// <inheritdoc cref="DrainCounter.Expect" />
    public void Expect(int count)
    {
        _reached.Reset();
        Volatile.Write(location: ref _target, value: Volatile.Read(ref _handled) + count);
    }

    public bool AwaitExpected(TimeSpan budget) => _reached.Wait(budget);

    private async Task PumpAsync()
    {
        await foreach (var @event in _events.Reader.ReadAllAsync())
        {
            if (@event is Bump(var by))
                Reactive.Sync(() => _state.Update(s => s with { Value = unchecked(s.Value + by) }));

            if (Interlocked.Increment(ref _handled) >= Volatile.Read(ref _target)) _reached.Set();
        }
    }
}
