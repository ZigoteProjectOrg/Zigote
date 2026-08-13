using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     The convenience layer over the core: <see cref="LinkedSignal{T}" /> (writable state that resets when
///     its source moves — Angular's <c>linkedSignal</c>, SignalsDotnet's <c>Signal.Linked</c>),
///     <see cref="Trigger" /> (a valueless "it happened" source), the multi-source
///     <see cref="ReactiveExtensions.ObserveAny" />, and the value-returning <c>Batch</c> overload.
/// </summary>
[Collection("Reactive-serial")]
public class LinkedSignalTests
{
    [Fact]
    public void A_linked_signal_starts_at_its_computed_value_and_is_writable()
    {
        var source = new Signal<int>(5);
        using var linked = Linked.From(() => source.Value * 2);

        Assert.Equal(10, linked.Value);

        linked.Value = 42; // manual override sticks
        Assert.Equal(42, linked.Value);
        Assert.Equal(42, linked.Peek());
    }

    [Fact]
    public void A_source_change_overrides_a_manual_write()
    {
        var source = new Signal<int>(5);
        using var linked = Linked.From(() => source.Value * 2);

        linked.Value = 42;
        source.Value = 10; // source moved → back to following it
        Assert.Equal(20, linked.Value);
    }

    [Fact]
    public void Reset_drops_a_manual_write_without_waiting_for_the_source()
    {
        var source = new Signal<int>(5);
        using var linked = Linked.From(() => source.Value * 2);

        linked.Value = 42;
        linked.Reset();
        Assert.Equal(10, linked.Value);
    }

    [Fact]
    public void A_linked_signal_is_a_dependency_like_any_other()
    {
        var source = new Signal<int>(1);
        using var linked = Linked.From(() => source.Value);
        using var doubled = Computed.From(() => linked.Value * 2);

        var fires = 0;
        using var sub = doubled.Observe(() => fires++);
        Assert.Equal(2, doubled.Value);

        linked.Value = 5; // manual write propagates
        Assert.Equal(10, doubled.Value);
        Assert.Equal(1, fires);

        source.Value = 7; // source-driven reset propagates
        Assert.Equal(14, doubled.Value);
        Assert.Equal(2, fires);
    }

    [Fact]
    public void Disposing_a_linked_signal_stops_it_following_but_keeps_the_value()
    {
        var source = new Signal<int>(1);
        var linked = Linked.From(() => source.Value);
        linked.Dispose();

        source.Value = 99;
        Assert.Equal(1, linked.Value); // no longer follows
        linked.Value = 3; // still a usable signal
        Assert.Equal(3, linked.Value);
    }

    [Fact]
    public void A_trigger_recomputes_dependents_without_carrying_a_value()
    {
        var reload = new Trigger();
        var runs = 0;
        using var c = Computed.From(() =>
            {
                reload.Depend();
                return ++runs;
            }
        );

        using var sub = c.Observe(() => { }); // watched, so it recomputes on fire
        Assert.Equal(1, c.Value);

        reload.Fire();
        Assert.Equal(2, c.Value);
        reload.Fire();
        Assert.Equal(3, c.Value);
    }

    [Fact]
    public void A_trigger_settles_an_effect_once_per_fire_and_coalesces_in_a_batch()
    {
        var reload = new Trigger();
        var runs = 0;
        using var e = new Effect(() =>
            {
                reload.Depend();
                runs++;
            }
        );
        Assert.Equal(1, runs);

        reload.Fire();
        Assert.Equal(2, runs);

        Reactive.Batch(() =>
            {
                reload.Fire();
                reload.Fire();
                reload.Fire();
            }
        );
        Assert.Equal(3, runs); // three fires, one re-run
    }

    [Fact]
    public void ObserveAny_fires_once_for_a_change_to_any_of_its_sources()
    {
        var a = new Signal<int>(0);
        var b = new Signal<int>(0);
        using var c = Computed.From(() => a.Value + b.Value);

        var fires = 0;
        using var sub = ReactiveExtensions.ObserveAny(
            () => fires++,
            a,
            b,
            c
        );
        Assert.Equal(0, fires); // observation only, no immediate call

        a.Value = 1; // a AND c changed → still one callback
        Assert.Equal(1, fires);

        b.Value = 2;
        Assert.Equal(2, fires);

        Reactive.Batch(() =>
            {
                a.Value = 10;
                b.Value = 20;
            }
        );
        Assert.Equal(3, fires);

        sub.Dispose();
        a.Value = 100;
        Assert.Equal(3, fires);
    }

    [Fact]
    public void Batch_can_return_a_value()
    {
        var a = new Signal<int>(1);
        var b = new Signal<int>(2);
        var runs = 0;
        using var e = new Effect(() =>
            {
                _ = a.Value + b.Value;
                runs++;
            }
        );

        var sum = Reactive.Batch(() =>
            {
                a.Value = 10;
                b.Value = 20;
                return a.Peek() + b.Peek();
            }
        );

        Assert.Equal(30, sum);
        Assert.Equal(2, runs); // still one drain for the whole batch
    }
}
