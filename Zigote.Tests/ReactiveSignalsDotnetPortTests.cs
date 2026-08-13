using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     Behaviours ported from the SignalsDotnet suite (github.com/fedeAlterio/SignalsDotnet,
///     <c>ComputedSignalTests</c> / <c>EffectTests</c>) that <see cref="ReactiveTests" /> did not
///     already
///     pin: exactly-once notification per write from either source, the full branch-switch sequence
///     (an abandoned dependency must go quiet, and come back when the branch returns), and the
///     "effects run at the end of the atomic operation" contract for <b>nested</b> batches — including
///     under concurrent independent graphs, where the shared graph lock and the shared batch depth are
///     the thing at risk. Their signal-per-property / async-computed / linked-signal tests have no
///     counterpart here and are deliberately not ported.
/// </summary>
[Collection(
    "Reactive-serial"
)] // shares process-static state (batch depth, GlobalVersion) with the other reactive tests
public class ReactiveSignalsDotnetPortTests
{
    [Fact]
    public void Computed_notifies_exactly_once_per_write_from_either_source()
    {
        var a = new Signal<int>(0);
        var b = new Signal<int>(0);
        using var sum = Computed.From(() => a.Value + b.Value);

        int fires = 0;
        using var _ = sum.Observe(() => fires++);

        a.Value = 2;
        Assert.Equal(expected: 1, actual: fires);
        Assert.Equal(expected: 2, actual: sum.Value);

        b.Value = 1;
        Assert.Equal(expected: 2, actual: fires);
        Assert.Equal(expected: 3, actual: sum.Value);
    }

    [Fact]
    public void Switching_branches_swaps_which_sources_notify()
    {
        var n1 = new Signal<int>(0);
        var n2 = new Signal<int>(0);
        var fallback = new Signal<int>(0);
        var useFallback = new Signal<bool>(false);

        using var c =
            Computed.From(() => useFallback.Value ? fallback.Value : n1.Value - n2.Value);
        Assert.Equal(expected: 0, actual: c.Value);

        int fires = 0;
        using var _ = c.Observe(() => fires++);

        fallback.Value = 2; // untaken branch → not a dependency
        Assert.Equal(expected: 0, actual: fires);

        useFallback.Value = true; // 0 → 2
        Assert.Equal(expected: 1, actual: fires);

        fallback.Value = 3; // now live
        Assert.Equal(expected: 2, actual: fires);

        useFallback.Value = false; // back to n1 - n2 == 0
        Assert.Equal(expected: 3, actual: fires);

        fallback.Value = 11; // abandoned again → silent
        Assert.Equal(expected: 3, actual: fires);

        n1.Value = 4; // the branch that is live again
        Assert.Equal(expected: 4, actual: fires);
        Assert.Equal(expected: 4, actual: c.Value);
    }

    [Fact]
    public void Effect_reruns_for_either_source_and_stops_after_dispose()
    {
        var n1 = new Signal<int>(0);
        var n2 = new Signal<int>(0);

        int sum = -1;
        var e = new Effect(() => sum = n1.Value + n2.Value);
        Assert.Equal(expected: 0, actual: sum);

        n1.Value = 1;
        Assert.Equal(expected: 1, actual: sum);
        n1.Value = 2;
        Assert.Equal(expected: 2, actual: sum);
        n2.Value = 2;
        Assert.Equal(expected: 4, actual: sum);

        e.Dispose();
        n2.Value = 3;
        Assert.Equal(expected: 4, actual: sum);
    }

    [Fact]
    public void Effect_runs_once_per_batch_regardless_of_write_count()
    {
        var n1 = new Signal<int>(0);
        var n2 = new Signal<int>(0);

        int runs = 0;
        using var e = new Effect(() =>
            {
                _ = n1.Value + n2.Value;
                runs++;
            }
        );
        Assert.Equal(expected: 1, actual: runs);

        Reactive.Batch(() =>
            {
                n2.Value = 4;
                n2.Value = 3;
                n1.Value = 4;
                n1.Value = 3;
                Assert.Equal(expected: 1, actual: runs); // nothing drains mid-batch
            }
        );
        Assert.Equal(expected: 2, actual: runs); // four writes, one re-run
    }

    [Fact]
    public void Nested_batches_defer_effects_to_the_outermost_exit()
    {
        var n1 = new Signal<int>(0);
        var n2 = new Signal<int>(0);

        int sum = -1;
        using var _ = new Effect(() => sum = n1.Value + n2.Value);
        Assert.Equal(expected: 0, actual: sum);

        Reactive.Batch(() =>
            {
                n1.Value = 1;
                Assert.Equal(expected: 0, actual: sum);
                n1.Value = 2;
                Assert.Equal(expected: 0, actual: sum);
            }
        );
        Assert.Equal(expected: 2, actual: sum);

        Reactive.Batch(() =>
            {
                n2.Value = 2;
                Assert.Equal(expected: 2, actual: sum);

                Reactive.Batch(() =>
                    {
                        n2.Value = 3;
                        Assert.Equal(expected: 2, actual: sum); // inner exit must NOT drain
                    }
                );

                Assert.Equal(expected: 2, actual: sum);
            }
        );
        Assert.Equal(expected: 5, actual: sum);
    }

    [Fact]
    public void Nested_batches_hold_under_concurrent_independent_graphs()
    {
        // Batch depth and the graph lock are process-wide; 33 threads each running the nested-batch
        // sequence over their OWN signals must not leak a drain into each other's open batch.
        int failures = 0;
        Parallel.For(
            fromInclusive: 0,
            toExclusive: 33,
            body: _ =>
            {
                var n1 = new Signal<int>(0);
                var n2 = new Signal<int>(0);
                int sum = -1;
                using var e = new Effect(() => sum = n1.Value + n2.Value);

                bool ok = sum == 0;
                Reactive.Batch(() =>
                    {
                        n1.Value = 1;
                        ok &= sum == 0;
                        n1.Value = 2;
                        ok &= sum == 0;
                    }
                );
                ok &= sum == 2;

                Reactive.Batch(() =>
                    {
                        n2.Value = 2;
                        ok &= sum == 2;
                        Reactive.Batch(() =>
                            {
                                n2.Value = 3;
                                ok &= sum == 2;
                            }
                        );
                        ok &= sum == 2;
                    }
                );
                ok &= sum == 5;

                if (!ok) Interlocked.Increment(ref failures);
            }
        );

        Assert.Equal(expected: 0, actual: failures);
    }
}
