using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     The positional (ordered-list) dependency reconcile in <see cref="Reaction" />: duplicate reads
///     collapse to one dependency slot, dependencies that merely change read order stay wired, and a
///     computed re-observed after a full detach reconnects through its recorded sources.
/// </summary>
[Collection("Reactive-serial")]
public class ReactiveReconcileTests
{
    [Fact]
    public void A_source_read_twice_collapses_to_one_dependency()
    {
        var a = new Signal<int>(1);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                return a.Value + a.Value;
            }
        );
        using var live = c.Observe(() => { });

        a.Value = 2;
        Assert.Equal(
            expected: 2,
            actual: runs
        ); // exactly one recompute per write — one edge, not two
        Assert.Equal(expected: 4, actual: c.Value);
    }

    [Fact]
    public void Dropping_a_duplicate_read_keeps_the_source_subscribed()
    {
        // Regression: with duplicate reads recorded as separate slots, the run that reads `a` once
        // would truncate a stale trailing `a` and unsubscribe it while still depending on it.
        var a = new Signal<int>(1);
        var twice = new Signal<bool>(true);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                return twice.Value ? a.Value + a.Value : a.Value;
            }
        );
        using var live = c.Observe(() => { });

        twice.Value = false;
        Assert.Equal(expected: 2, actual: runs);

        a.Value = 7; // a must still be wired after the branch change
        Assert.Equal(expected: 3, actual: runs);
        Assert.Equal(expected: 7, actual: c.Value);
    }

    [Fact]
    public void Dependencies_that_swap_read_order_stay_subscribed()
    {
        var flip = new Signal<bool>(false);
        var a = new Signal<int>(1);
        var b = new Signal<int>(10);
        using var c = Computed.From(() => flip.Value ? b.Value + a.Value : a.Value + b.Value);
        int fires = 0;
        using var live = c.Observe(() => fires++);

        flip.Value = true; // same sources, swapped positions — the value is unchanged
        Assert.Equal(expected: 0, actual: fires);

        a.Value = 2;
        Assert.Equal(expected: 1, actual: fires);
        Assert.Equal(expected: 12, actual: c.Value);
        b.Value = 20;
        Assert.Equal(expected: 2, actual: fires);
        Assert.Equal(expected: 22, actual: c.Value);
    }

    [Fact]
    public void Reobserving_a_detached_computed_with_no_intervening_writes_rewires_it()
    {
        var s = new Signal<int>(1);
        using var c = Computed.From(() => s.Value * 2);
        var sub = c.Observe(() => { });
        sub.Dispose(); // last observer gone → detached from s

        int fires = 0;
        using var sub2 = c.Observe(() => fires++); // re-observe with nothing changed in between

        s.Value = 5; // must reach c through the rewired edge
        Assert.Equal(expected: 1, actual: fires);
        Assert.Equal(expected: 10, actual: c.Value);
    }
}
