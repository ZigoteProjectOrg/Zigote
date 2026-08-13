using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     What the graph does when a body misbehaves — the preact-signals contracts the core was missing.
///     A computed's exception is cached like a value (delivered on read, recomputed only when a
///     dependency
///     moves) instead of re-running the failing body on every read, which is what turns a throwing
///     computed
///     inside a paint binding into a per-frame recompute cliff. A computed read while it is still
///     computing
///     is a cycle and throws, instead of quietly handing back the previous value. And a reaction that
///     threw
///     is still rescheduled by the next change — one failure must not silence it forever.
/// </summary>
[Collection("Reactive-serial")]
public class ReactiveFailureTests
{
    [Fact]
    public void A_failing_computed_is_delivered_on_read_not_at_construction()
    {
        int runs = 0;
        var c = Computed.From<int>(() =>
            {
                runs++;
                throw new InvalidOperationException("boom");
            }
        );

        Assert.Equal(expected: 1, actual: runs); // eager first compute still happened
        Assert.Throws<InvalidOperationException>(() => c.Value);
        c.Dispose();
    }

    [Fact]
    public void A_throwing_computed_runs_once_and_rethrows_the_cached_error_on_later_reads()
    {
        var s = new Signal<int>(1);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                if (s.Value < 10) throw new InvalidOperationException("boom");
                return s.Value;
            }
        );

        Assert.Equal(expected: 1, actual: runs);
        for (int i = 0; i < 5; i++) Assert.Throws<InvalidOperationException>(() => c.Value);
        Assert.Throws<InvalidOperationException>(() => c.Peek());
        Assert.Equal(expected: 1, actual: runs); // six reads, zero extra runs of the failing body
    }

    [Fact]
    public void A_dependency_change_retries_a_failed_computed_and_it_recovers()
    {
        var s = new Signal<int>(1);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                if (s.Value < 10) throw new InvalidOperationException("boom");
                return s.Value * 2;
            }
        );

        Assert.Throws<InvalidOperationException>(() => c.Value);
        Assert.Equal(expected: 1, actual: runs);

        s.Value = 2; // still failing, but a dependency moved → exactly one retry
        Assert.Throws<InvalidOperationException>(() => c.Value);
        Assert.Equal(expected: 2, actual: runs);

        s.Value = 20; // now it succeeds
        Assert.Equal(expected: 40, actual: c.Value);
        Assert.Equal(expected: 3, actual: runs);
    }

    [Fact]
    public void An_observed_computed_that_recovers_notifies_its_observers()
    {
        // Recovery must wake observers even when the value equals the last good one: they were told the
        // computed changed when it threw, so "readable again" is also a change.
        var s = new Signal<int>(5);
        var fail = new Signal<bool>(false);
        using var c = Computed.From(() =>
            fail.Value ? throw new InvalidOperationException("boom") : s.Value
        );

        int fires = 0;
        using var sub = c.Observe(() => fires++);
        Assert.Equal(expected: 5, actual: c.Value);

        fail.Value = true;
        Assert.Equal(expected: 1, actual: fires);
        Assert.Throws<InvalidOperationException>(() => c.Value);

        fail.Value = false; // same value as before the failure
        Assert.Equal(expected: 2, actual: fires);
        Assert.Equal(expected: 5, actual: c.Value);
    }

    [Fact]
    public void An_effect_that_threw_still_reruns_on_the_next_change()
    {
        // Regression: a failed run left the reaction Dirty-but-unscheduled, so MarkStale's "already stale"
        // bail meant it never ran again — one throw silenced the effect for the rest of the process.
        var s = new Signal<int>(0);
        int runs = 0;
        int seen = -1;
        var previous = Reactive.OnError;
        Reactive.OnError = _ => { };
        try
        {
            using var e = new Effect(() =>
                {
                    runs++;
                    int v = s.Value;
                    if (v == 1) throw new InvalidOperationException("boom");
                    seen = v;
                }
            );
            Assert.Equal(expected: 1, actual: runs);

            s.Value = 1; // throws
            Assert.Equal(expected: 2, actual: runs);

            s.Value = 2; // must still be scheduled
            Assert.Equal(expected: 3, actual: runs);
            Assert.Equal(expected: 2, actual: seen);
        }
        finally
        {
            Reactive.OnError = previous;
        }
    }

    [Fact]
    public void An_effect_reading_a_failing_computed_is_isolated_and_recovers()
    {
        var s = new Signal<int>(0);
        var fail = new Signal<bool>(false);
        using var c = Computed.From(() =>
            fail.Value ? throw new InvalidOperationException("boom") : s.Value
        );

        int errors = 0;
        var previous = Reactive.OnError;
        Reactive.OnError = _ => errors++;
        try
        {
            int seen = -1;
            using var e = new Effect(() => seen = c.Value);
            Assert.Equal(expected: 0, actual: seen);

            fail.Value = true; // the effect's read rethrows → reported, not unwound into the writer
            Assert.Equal(expected: 1, actual: errors);

            fail.Value = false;
            s.Value = 7;
            Assert.Equal(expected: 7, actual: seen);
            Assert.Equal(expected: 1, actual: errors);
        }
        finally
        {
            Reactive.OnError = previous;
        }
    }

    [Fact]
    public void A_computed_that_reads_itself_throws_instead_of_returning_a_stale_value()
    {
        var s = new Signal<int>(0);
        Computed<int>? self = null;
        // The first (construction) run takes the null branch and records `s` as the dependency; the write
        // below forces a recompute whose body re-enters the computed while it is running.
        self = Computed.From(() => self is null ? s.Value : self.Value + s.Value);

        s.Value = 1;
        var ex = Assert.Throws<InvalidOperationException>(() => self.Value);
        Assert.Contains(
            expectedSubstring: "cycle",
            actualString: ex.Message,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
        self.Dispose();
    }

    [Fact]
    public void Mutually_recursive_computeds_throw_instead_of_returning_a_stale_value()
    {
        var s = new Signal<int>(0);
        Computed<int>? a = null;
        using var b = Computed.From(() => a is null ? s.Value : a.Value + 1);
        a = Computed.From(() => b.Value + 1);

        s.Value = 1; // invalidates both → the next read walks a → b → a
        var ex = Assert.Throws<InvalidOperationException>(() => a.Value);
        Assert.Contains(
            expectedSubstring: "cycle",
            actualString: ex.Message,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
        a.Dispose();
    }

    /// <summary>
    ///     The convergence guard counts WAVES of effects, not effects: a signal with more subscribers
    ///     than the guard's limit converges in one wave and must not be mistaken for a cycle. It was —
    ///     any signal read by more than 100 effects (a broadcast to a grid of reactive cells) threw
    ///     "did not converge" on every write.
    /// </summary>
    [Fact]
    public void A_wide_fan_out_is_not_mistaken_for_a_cycle()
    {
        var s = new Signal<int>(0);
        int runs = 0;
        var effects = new List<Effect>(500);
        for (int i = 0; i < 500; i++)
        {
            effects.Add(
                new Effect(() =>
                    {
                        _ = s.Value;
                        runs++;
                    }
                )
            );
        }

        runs = 0;
        s.Value = 1;
        Assert.Equal(expected: 500, actual: runs);

        foreach (var e in effects) e.Dispose();
    }
}
