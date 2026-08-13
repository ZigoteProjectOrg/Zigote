using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     The convenience layer over the core: <see cref="LinkedSignal{T}" /> (writable state that resets
///     when
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

        Assert.Equal(expected: 10, actual: linked.Value);

        linked.Value = 42; // manual override sticks
        Assert.Equal(expected: 42, actual: linked.Value);
        Assert.Equal(expected: 42, actual: linked.Peek());
    }

    [Fact]
    public void A_source_change_overrides_a_manual_write()
    {
        var source = new Signal<int>(5);
        using var linked = Linked.From(() => source.Value * 2);

        linked.Value = 42;
        source.Value = 10; // source moved → back to following it
        Assert.Equal(expected: 20, actual: linked.Value);
    }

    [Fact]
    public void Reset_drops_a_manual_write_without_waiting_for_the_source()
    {
        var source = new Signal<int>(5);
        using var linked = Linked.From(() => source.Value * 2);

        linked.Value = 42;
        linked.Reset();
        Assert.Equal(expected: 10, actual: linked.Value);
    }

    [Fact]
    public void A_linked_signal_is_a_dependency_like_any_other()
    {
        var source = new Signal<int>(1);
        using var linked = Linked.From(() => source.Value);
        using var doubled = Computed.From(() => linked.Value * 2);

        int fires = 0;
        using var sub = doubled.Observe(() => fires++);
        Assert.Equal(expected: 2, actual: doubled.Value);

        linked.Value = 5; // manual write propagates
        Assert.Equal(expected: 10, actual: doubled.Value);
        Assert.Equal(expected: 1, actual: fires);

        source.Value = 7; // source-driven reset propagates
        Assert.Equal(expected: 14, actual: doubled.Value);
        Assert.Equal(expected: 2, actual: fires);
    }

    [Fact]
    public void Disposing_a_linked_signal_stops_it_following_but_keeps_the_value()
    {
        var source = new Signal<int>(1);
        var linked = Linked.From(() => source.Value);
        linked.Dispose();

        source.Value = 99;
        Assert.Equal(expected: 1, actual: linked.Value); // no longer follows
        linked.Value = 3; // still a usable signal
        Assert.Equal(expected: 3, actual: linked.Value);
    }

    [Fact]
    public void A_trigger_recomputes_dependents_without_carrying_a_value()
    {
        var reload = new Trigger();
        int runs = 0;
        using var c = Computed.From(() =>
            {
                reload.Depend();
                return ++runs;
            }
        );

        using var sub = c.Observe(() => { }); // watched, so it recomputes on fire
        Assert.Equal(expected: 1, actual: c.Value);

        reload.Fire();
        Assert.Equal(expected: 2, actual: c.Value);
        reload.Fire();
        Assert.Equal(expected: 3, actual: c.Value);
    }

    [Fact]
    public void A_trigger_settles_an_effect_once_per_fire_and_coalesces_in_a_batch()
    {
        var reload = new Trigger();
        int runs = 0;
        using var e = new Effect(() =>
            {
                reload.Depend();
                runs++;
            }
        );
        Assert.Equal(expected: 1, actual: runs);

        reload.Fire();
        Assert.Equal(expected: 2, actual: runs);

        Reactive.Batch(() =>
            {
                reload.Fire();
                reload.Fire();
                reload.Fire();
            }
        );
        Assert.Equal(expected: 3, actual: runs); // three fires, one re-run
    }

    [Fact]
    public void ObserveAny_fires_once_for_a_change_to_any_of_its_sources()
    {
        var a = new Signal<int>(0);
        var b = new Signal<int>(0);
        using var c = Computed.From(() => a.Value + b.Value);

        int fires = 0;
        using var sub = ReactiveExtensions.ObserveAny(
            onChanged: () => fires++,
            a,
            b,
            c
        );
        Assert.Equal(expected: 0, actual: fires); // observation only, no immediate call

        a.Value = 1; // a AND c changed → still one callback
        Assert.Equal(expected: 1, actual: fires);

        b.Value = 2;
        Assert.Equal(expected: 2, actual: fires);

        Reactive.Batch(() =>
            {
                a.Value = 10;
                b.Value = 20;
            }
        );
        Assert.Equal(expected: 3, actual: fires);

        sub.Dispose();
        a.Value = 100;
        Assert.Equal(expected: 3, actual: fires);
    }

    [Fact]
    public void Batch_can_return_a_value()
    {
        var a = new Signal<int>(1);
        var b = new Signal<int>(2);
        int runs = 0;
        using var e = new Effect(() =>
            {
                _ = a.Value + b.Value;
                runs++;
            }
        );

        int sum = Reactive.Batch(() =>
            {
                a.Value = 10;
                b.Value = 20;
                return a.Peek() + b.Peek();
            }
        );

        Assert.Equal(expected: 30, actual: sum);
        Assert.Equal(expected: 2, actual: runs); // still one drain for the whole batch
    }
}
