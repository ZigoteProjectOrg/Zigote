// The pump is a concurrency primitive, so several tests here drive it from more than one thread and
// use bounded waits as the assertion — a stranded event or a stalled pump must fail on the timeout
// rather than hang the run. Awaiting instead would defeat the point of the ones that check the
// *synchronous* path: that Add has already run the handler by the time it returns.

#pragma warning disable xUnit1031, xUnit1051
using Xunit;
using Zigote.Bloc;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     <see cref="Bloc{TEvent}" />'s event pump: ordering, the synchronous fast path, the handoff to
///     and back from an awaiting handler, failure isolation, disposal, and the observation hooks.
///     <para>
///         These are the guarantees <c>Zigote.Bloc/README.md</c> sells to apps outside this repo, and
///         most of them are properties of the interleaving rather than of a single call — "an Add
///         that lands while the pump is between events is not stranded" cannot be read off the code
///         with any confidence, which is exactly why it is here.
///     </para>
/// </summary>
[Collection("Bloc-serial")] // BlocErrors/BlocObserver are process-static hooks
public class BlocTests : IDisposable
{
    public void Dispose()
    {
        BlocErrors.OnError = null;
        BlocObserver.OnEvent = null;
        BlocObserver.OnChange = null;
    }

    [Fact]
    public void Add_runs_the_handler_before_it_returns_when_nothing_awaits()
    {
        using var bloc = new CounterBloc();

        bloc.Add(new Bump(1));

        // No polling, no pumping the loop: the state is already there.
        Assert.Equal(1, bloc.Current.Value);
    }

    [Fact]
    public void Add_from_inside_a_handler_runs_after_it_not_nested_inside_it()
    {
        using var bloc = new OrderBloc();

        bloc.Add(new Outer());

        // Nested dispatch would give outer-start, inner, outer-end.
        Assert.Equal(new[] { "outer:start", "outer:end", "inner" }, bloc.Log);
    }

    [Fact]
    public void Events_added_while_a_handler_awaits_wait_their_turn_and_keep_their_order()
    {
        var release = new ManualResetEventSlim();
        var drained = new ManualResetEventSlim();
        using var bloc = new OrderBloc { Gate = release, Drained = drained };

        bloc.Add(new Slow());
        // The handler is parked on the gate, so these queue behind it rather than interleaving.
        bloc.Add(new Note("a"));
        bloc.Add(new Note("b"));
        Assert.Equal(new[] { "slow:start" }, bloc.Log);

        release.Set();
        Assert.True(drained.Wait(TimeSpan.FromSeconds(5)), "pump did not resume after the await");

        Assert.Equal(new[] { "slow:start", "slow:end", "a", "b" }, bloc.Log);
    }

    [Fact]
    public void An_add_racing_the_end_of_a_drain_is_not_stranded()
    {
        // The window this covers: Pump checks the queue, finds it empty, and is about to drop the
        // pumping flag. An Add landing there must either be seen by that check or start its own
        // pump — if both sides decide the other will handle it, the event sits in the queue until
        // some later Add happens to arrive, which in an app looks like one dead tap in a hundred.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var bloc = new CounterBloc();
            using var start = new ManualResetEventSlim();

            var other = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 50; i++) bloc.Add(new Bump(1));
            });

            start.Set();
            for (var i = 0; i < 50; i++) bloc.Add(new Bump(1));

            Assert.True(other.Wait(TimeSpan.FromSeconds(5)), "producer stalled");
            Assert.True(SpinWait.SpinUntil(() => bloc.Current.Value == 100, TimeSpan.FromSeconds(5)),
                $"attempt {attempt}: stranded event — {bloc.Current.Value} of 100 handled");
        }
    }

    [Fact]
    public void A_throwing_handler_is_reported_and_the_pump_carries_on()
    {
        var failures = new List<string>();
        BlocErrors.OnError = (_, context) => failures.Add(context);

        using var bloc = new CounterBloc();

        bloc.Add(new Boom());
        bloc.Add(new Bump(1));

        Assert.Single(failures);
        Assert.Contains("Boom", failures[0]);
        Assert.Equal(1, bloc.Current.Value); // the event behind the failure still ran
    }

    [Fact]
    public void A_handler_that_throws_after_awaiting_is_reported_and_the_pump_carries_on()
    {
        // Different code path from the synchronous throw: the exception is on the returned ValueTask,
        // so only the async drain can see it.
        var failures = new List<string>();
        BlocErrors.OnError = (_, context) => failures.Add(context);

        using var bloc = new CounterBloc();

        bloc.Add(new BoomAsync());
        Assert.True(SpinWait.SpinUntil(() => failures.Count == 1, TimeSpan.FromSeconds(5)),
            "async failure never reported");

        bloc.Add(new Bump(1));
        Assert.True(SpinWait.SpinUntil(() => bloc.Current.Value == 1, TimeSpan.FromSeconds(5)),
            "pump did not resume after an async failure");
    }

    [Fact]
    public void A_throwing_error_hook_does_not_kill_the_pump()
    {
        BlocErrors.OnError = (_, _) => throw new InvalidOperationException("reporter is broken");

        using var bloc = new CounterBloc();

        bloc.Add(new Boom());
        bloc.Add(new Bump(1));

        Assert.Equal(1, bloc.Current.Value);
    }

    [Fact]
    public void Dispose_cancels_the_lifetime_drops_later_events_and_releases_tracked_subscriptions()
    {
        var bloc = new CounterBloc();
        var wire = new Probe();
        bloc.TrackPublic(wire);

        bloc.Add(new Bump(1));
        bloc.Dispose();

        Assert.True(wire.Disposed);
        Assert.True(bloc.LifetimeToken.IsCancellationRequested);

        bloc.Add(new Bump(1)); // dropped, not thrown
        Assert.Equal(1, bloc.Current.Value);

        bloc.Dispose(); // idempotent
    }

    [Fact]
    public void Track_after_dispose_disposes_the_subscription_instead_of_holding_it()
    {
        var bloc = new CounterBloc();
        bloc.Dispose();

        var wire = new Probe();
        bloc.TrackPublic(wire);

        Assert.True(wire.Disposed);
    }

    [Fact]
    public void Restart_cancels_the_previous_unit_of_work()
    {
        using var bloc = new CounterBloc();

        var first = bloc.RestartPublic();
        Assert.False(first.IsCancellationRequested);

        var second = bloc.RestartPublic();
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void Restart_after_dispose_hands_back_an_already_cancelled_token()
    {
        var bloc = new CounterBloc();
        bloc.Dispose();

        Assert.True(bloc.RestartPublic().IsCancellationRequested);
    }

    [Fact]
    public void Emitting_an_equal_state_does_not_wake_watchers()
    {
        using var bloc = new CounterBloc();

        var rebuilds = 0;
        using var _ = bloc.State.Observe(() => rebuilds++);

        bloc.Add(new Bump(1));
        Assert.Equal(1, rebuilds);

        bloc.Add(new Bump(0)); // same record value → deduplicated by the signal
        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public void Select_projects_one_fact_and_only_fires_when_that_fact_moves()
    {
        using var bloc = new CounterBloc();
        using var busy = bloc.Select(s => s.Busy);

        var fires = 0;
        using var _ = busy.Observe(() => fires++);

        bloc.Add(new Bump(1)); // Value moves, Busy does not
        bloc.Add(new Bump(1));
        Assert.Equal(0, fires);

        bloc.Add(new SetBusy(true));
        Assert.Equal(1, fires);
    }

    [Fact]
    public void Select_keeps_answering_after_the_bloc_is_disposed()
    {
        // The bloc deliberately does not own the projections it hands out: an unobserved computed
        // detaches from the state signal by itself, so dropping one is not a leak, and one that is
        // still held reads the final state instead of throwing at paint time.
        var bloc = new CounterBloc();
        using var selected = bloc.Select(s => s.Value);

        bloc.Add(new Bump(7));
        bloc.Dispose();

        Assert.Equal(7, selected.Value);
    }

    [Fact]
    public void The_observer_sees_events_and_transitions_interleaved_in_order()
    {
        var timeline = new List<string>();
        // Not GetType().Name: these events are file-local types, so the runtime name is mangled.
        BlocObserver.OnEvent = (_, e) => timeline.Add($"event:{(e is Bump b ? $"Bump({b.By})" : "?")}");
        BlocObserver.OnChange = (_, from, to) => timeline.Add($"change:{((CounterState)from!).Value}→{((CounterState)to!).Value}");

        using var bloc = new CounterBloc();
        bloc.Add(new Bump(1));
        bloc.Add(new Bump(2));

        Assert.Equal(new[] { "event:Bump(1)", "change:0→1", "event:Bump(2)", "change:1→3" }, timeline);
    }

    [Fact]
    public void The_observer_does_not_report_a_deduplicated_emit_as_a_transition()
    {
        var changes = 0;
        BlocObserver.OnChange = (_, _, _) => changes++;

        using var bloc = new CounterBloc();
        bloc.Add(new Bump(0)); // emits the state it is already in

        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_throwing_observer_is_reported_and_the_event_is_still_handled()
    {
        var failures = new List<string>();
        BlocErrors.OnError = (_, context) => failures.Add(context);
        BlocObserver.OnEvent = (_, _) => throw new InvalidOperationException("observer is broken");
        BlocObserver.OnChange = (_, _, _) => throw new InvalidOperationException("observer is broken");

        using var bloc = new CounterBloc();
        bloc.Add(new Bump(1));

        Assert.Equal(1, bloc.Current.Value);
        Assert.Equal(2, failures.Count); // one for each hook
    }

    private sealed class Probe : IDisposable
    {
        public bool Disposed;

        public void Dispose()
        {
            Disposed = true;
        }
    }
}

file record CounterState(int Value, bool Busy);

file abstract record CounterEvent;

file sealed record Bump(int By) : CounterEvent;

file sealed record SetBusy(bool Value) : CounterEvent;

file sealed record Boom : CounterEvent;

file sealed record BoomAsync : CounterEvent;

file sealed class CounterBloc() : Bloc<CounterEvent, CounterState>(new CounterState(0, false))
{
    public CancellationToken LifetimeToken => Lifetime;

    public void TrackPublic(IDisposable subscription)
    {
        Track(subscription);
    }

    public CancellationToken RestartPublic()
    {
        return Restart();
    }

    protected override async ValueTask OnEventAsync(CounterEvent @event, CancellationToken ct)
    {
        switch (@event)
        {
            case Bump(var by):
                Emit(Current with { Value = Current.Value + by });
                break;
            case SetBusy(var value):
                Emit(Current with { Busy = value });
                break;
            case Boom:
                throw new InvalidOperationException("Boom");
            case BoomAsync:
                await Task.Yield();
                throw new InvalidOperationException("BoomAsync");
        }
    }
}

file abstract record OrderEvent;

file sealed record Outer : OrderEvent;

file sealed record Inner : OrderEvent;

file sealed record Slow : OrderEvent;

file sealed record Note(string Text) : OrderEvent;

file sealed class OrderBloc() : Bloc<OrderEvent, int>(0)
{
    private readonly List<string> _log = [];

    public ManualResetEventSlim? Gate { get; init; }
    public ManualResetEventSlim? Drained { get; init; }

    public string[] Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToArray();
            }
        }
    }

    protected override async ValueTask OnEventAsync(OrderEvent @event, CancellationToken ct)
    {
        switch (@event)
        {
            case Outer:
                Write("outer:start");
                Add(new Inner());
                Write("outer:end");
                break;
            case Inner:
                Write("inner");
                break;
            case Slow:
                Write("slow:start");
                await Task.Run(() => Gate!.Wait(TimeSpan.FromSeconds(5)), ct);
                Write("slow:end");
                break;
            case Note(var text):
                Write(text);
                if (text == "b") Drained?.Set();
                break;
        }
    }

    private void Write(string entry)
    {
        lock (_log)
        {
            _log.Add(entry);
        }
    }
}
