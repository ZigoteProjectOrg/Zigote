// Every test here drives the pump from several threads and uses a bounded wait as the assertion: a
// stranded event, a stalled pump or a deadlock must fail on the timeout rather than hang the run.
// Awaiting instead would defeat the tests that are specifically about the synchronous path.

#pragma warning disable xUnit1031, xUnit1051
using System.Collections.Concurrent;
using Xunit;
using Zigote.Bloc;

namespace Zigote.Tests;

/// <summary>
///     The pump under contention. <see cref="BlocTests" /> establishes the single-threaded contract;
///     this asks whether it still holds when producers arrive from everywhere — which is the normal
///     case for a bloc, not an exotic one: a repository callback, a socket, a frame timer and a tap
///     all reach the same <see cref="Bloc{TEvent}.Add" /> from different threads.
///     <para>
///         These assert rather than report. Zigote.Bloc claims ordering, mutual exclusion of handlers
///         and a clean shutdown; a claim the suite only observes is a claim nobody is holding.
///     </para>
/// </summary>
[Collection("Bloc-serial")] // BlocErrors/BlocObserver are process-static hooks
public class BlocConcurrencyTests : IDisposable
{
    private const int Producers = 8;
    private const int PerProducer = 5_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        BlocErrors.OnError = null;
        BlocObserver.OnEvent = null;
        BlocObserver.OnChange = null;
    }

    [Fact]
    public void Every_event_from_every_producer_is_handled_exactly_once()
    {
        using var bloc = new TallyBloc();

        RunProducers((producer, i) => bloc.Add(new Tagged(producer, i)));

        Assert.True(bloc.AwaitHandled(Producers * PerProducer, Budget),
            $"only {bloc.Handled} of {Producers * PerProducer} events were handled");
        Assert.Empty(bloc.Duplicates);
    }

    [Fact]
    public void Events_from_one_producer_keep_their_relative_order()
    {
        // Interleaving between producers is whatever the scheduler decides; within one producer the
        // queue must not reorder, or "typed, then submitted" can arrive as "submitted, then typed".
        using var bloc = new TallyBloc();

        RunProducers((producer, i) => bloc.Add(new Tagged(producer, i)));
        Assert.True(bloc.AwaitHandled(Producers * PerProducer, Budget), "pump did not drain");

        foreach (var (producer, sequence) in bloc.PerProducer())
        {
            Assert.Equal(PerProducer, sequence.Count);
            for (var i = 0; i < sequence.Count; i++)
                Assert.True(sequence[i] == i,
                    $"producer {producer} saw {sequence[i]} at position {i} — the queue reordered it");
        }
    }

    [Fact]
    public void Handlers_never_run_concurrently_even_when_the_pump_changes_threads()
    {
        // The guarantee the whole design rests on: business logic gets to be single-threaded. The
        // handler awaits often enough that the pump resumes on pool threads rather than the callers'.
        using var bloc = new OverlapBloc();

        RunProducers((_, _) => bloc.Add(new Tagged(0, 0)), perProducer: 500);

        Assert.True(bloc.AwaitHandled(Producers * 500, Budget), "pump did not drain");
        Assert.Equal(1, bloc.MaxConcurrent);
    }

    [Fact]
    public void Dispose_racing_a_write_storm_neither_throws_nor_hangs()
    {
        var escaped = new ConcurrentBag<Exception>();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var bloc = new TallyBloc();
            var stop = false;

            var producers = Enumerable.Range(0, Producers).Select(p => Task.Factory.StartNew(
                    () =>
                    {
                        for (var i = 0; !Volatile.Read(ref stop) && i < PerProducer; i++)
                            try
                            {
                                bloc.Add(new Tagged(p, i));
                            }
                            catch (Exception ex)
                            {
                                escaped.Add(ex); // Add is documented never to throw, dead or alive
                            }
                    },
                    TaskCreationOptions.LongRunning
                )
            ).ToArray();

            Thread.Yield();
            bloc.Dispose();
            Volatile.Write(ref stop, true);

            Assert.True(Task.WaitAll(producers, Budget), $"attempt {attempt}: producers did not finish");
        }

        Assert.Empty(escaped);
    }

    [Fact]
    public void A_handler_resuming_after_dispose_sees_a_cancelled_lifetime()
    {
        // Dispose is what cancels the await a parked handler is sitting on, so "resume into a dead
        // bloc" is the ordinary path out of a handler, not an edge case. Reading Lifetime there used
        // to throw ObjectDisposedException, because Dispose disposes the token source.
        using var parked = new ManualResetEventSlim();
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bloc = new ParkingBloc(parked, observed);

        bloc.Add(new Tagged(0, 0));
        Assert.True(SpinWait.SpinUntil(() => bloc.Parked, Budget), "handler never parked");

        bloc.Dispose();
        parked.Set();

        Assert.True(observed.Task.Wait(Budget), "handler never resumed");
        Assert.True(observed.Task.Result, "Lifetime did not read as cancelled after dispose");
    }

    [Fact]
    public void Restart_racing_dispose_does_not_throw()
    {
        // Restart publishes a new token source and Dispose tears the current one down; both use an
        // Interlocked exchange, so they can each end up holding the other's instance.
        var escaped = new ConcurrentBag<Exception>();

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var bloc = new TallyBloc();
            var stop = false;

            var spinner = Task.Factory.StartNew(
                () =>
                {
                    while (!Volatile.Read(ref stop))
                        try
                        {
                            _ = bloc.RestartPublic();
                        }
                        catch (Exception ex)
                        {
                            escaped.Add(ex);
                        }
                },
                TaskCreationOptions.LongRunning
            );

            Thread.Yield();
            bloc.Dispose();
            Volatile.Write(ref stop, true);
            Assert.True(spinner.Wait(Budget), $"attempt {attempt}: spinner did not finish");
        }

        Assert.Empty(escaped);
    }

    [Fact]
    public void The_observer_records_every_event_and_an_unbroken_chain_of_transitions()
    {
        // Emits are serialised on the pump, so the timeline must read as one contiguous chain: every
        // transition starts where the previous one ended. A gap means two handlers overlapped, or a
        // state was published that the timeline never saw.
        var events = 0;
        var transitions = new List<(int From, int To)>();

        BlocObserver.OnEvent = (_, _) => Interlocked.Increment(ref events);
        BlocObserver.OnChange = (_, from, to) =>
        {
            lock (transitions) transitions.Add((((TallyState)from!).Handled, ((TallyState)to!).Handled));
        };

        using var bloc = new TallyBloc();
        RunProducers((producer, i) => bloc.Add(new Tagged(producer, i)));
        Assert.True(bloc.AwaitHandled(Producers * PerProducer, Budget), "pump did not drain");

        Assert.Equal(Producers * PerProducer, Volatile.Read(ref events));

        lock (transitions)
        {
            Assert.Equal(Producers * PerProducer, transitions.Count);
            for (var i = 1; i < transitions.Count; i++)
                Assert.True(transitions[i].From == transitions[i - 1].To,
                    $"timeline breaks at {i}: {transitions[i - 1]} then {transitions[i]}");
        }
    }

    private static void RunProducers(Action<int, int> add, int perProducer = PerProducer)
    {
        var tasks = new Task[Producers];
        for (var p = 0; p < Producers; p++)
        {
            var producer = p;
            tasks[p] = Task.Factory.StartNew(
                () =>
                {
                    for (var i = 0; i < perProducer; i++) add(producer, i);
                },
                TaskCreationOptions.LongRunning
            );
        }

        Assert.True(Task.WaitAll(tasks, Budget), "producers did not finish");
    }
}

file sealed record Tagged(int Producer, int Sequence);

file sealed record TallyState(int Handled);

/// <summary>Records what it handled, so ordering and completeness are both readable afterwards.</summary>
file sealed class TallyBloc() : SyncBloc<Tagged, TallyState>(new TallyState(0))
{
    private readonly ManualResetEventSlim _reached = new();
    private readonly List<Tagged> _seen = [];
    private int _handled;
    private int _target = int.MaxValue;

    public int Handled => Volatile.Read(ref _handled);

    /// <summary>Any (producer, sequence) pair handled more than once — must always be empty.</summary>
    public IReadOnlyList<Tagged> Duplicates
    {
        get
        {
            lock (_seen)
            {
                return _seen.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            }
        }
    }

    public CancellationToken RestartPublic()
    {
        return Restart();
    }

    public bool AwaitHandled(int count, TimeSpan budget)
    {
        Volatile.Write(ref _target, count);
        if (Handled >= count) return true; // already there; nobody will set the event again
        return _reached.Wait(budget);
    }

    public IEnumerable<(int Producer, List<int> Sequence)> PerProducer()
    {
        lock (_seen)
        {
            return _seen.GroupBy(t => t.Producer)
                .Select(g => (g.Key, g.Select(t => t.Sequence).ToList()))
                .ToArray();
        }
    }

    protected override void OnEvent(Tagged @event)
    {
        lock (_seen)
        {
            _seen.Add(@event);
        }

        var handled = Interlocked.Increment(ref _handled);
        Emit(new TallyState(handled));
        if (handled >= Volatile.Read(ref _target)) _reached.Set();
    }

    protected override void OnDispose()
    {
        _reached.Dispose();
    }
}

/// <summary>Awaits inside the handler, so the pump resumes on pool threads and any overlap shows up.</summary>
file sealed class OverlapBloc() : Bloc<Tagged, int>(0)
{
    private readonly ManualResetEventSlim _reached = new();
    private int _concurrent;
    private int _handled;
    private int _max;
    private int _target = int.MaxValue;

    public int MaxConcurrent => Volatile.Read(ref _max);

    public bool AwaitHandled(int count, TimeSpan budget)
    {
        Volatile.Write(ref _target, count);
        if (Volatile.Read(ref _handled) >= count) return true;
        return _reached.Wait(budget);
    }

    protected override async ValueTask OnEventAsync(Tagged @event, CancellationToken ct)
    {
        var inside = Interlocked.Increment(ref _concurrent);
        InterlockedMax(ref _max, inside);

        await Task.Yield(); // the pump comes back on a different thread

        InterlockedMax(ref _max, Volatile.Read(ref _concurrent));
        Interlocked.Decrement(ref _concurrent);

        var handled = Interlocked.Increment(ref _handled);
        if (handled >= Volatile.Read(ref _target)) _reached.Set();
    }

    protected override void OnDispose()
    {
        _reached.Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
                return;
    }
}

/// <summary>Parks inside the handler until released, so the test can dispose the bloc underneath it.</summary>
file sealed class ParkingBloc(ManualResetEventSlim release, TaskCompletionSource<bool> observed)
    : Bloc<Tagged, int>(0)
{
    private volatile bool _parked;

    public bool Parked => _parked;

    protected override async ValueTask OnEventAsync(Tagged @event, CancellationToken ct)
    {
        _parked = true;
        await Task.Run(() => release.Wait(TimeSpan.FromSeconds(30)), CancellationToken.None);

        try
        {
            observed.TrySetResult(Lifetime.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            observed.TrySetException(ex);
        }
    }
}
