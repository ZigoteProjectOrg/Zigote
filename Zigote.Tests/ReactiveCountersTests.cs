using Xunit;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Debug;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Tests;

/// <summary>
///     The diagnostic counters behind devtools' <c>reactive.*</c> variables (docs/architecture.md,
///     "Diagnostics"). They are only useful if they count the thing their name claims — an idle graph
///     that keeps ticking would send someone hunting a churn that isn't there, and one that misses a
///     real recompute hides the churn that is.
/// </summary>
[Collection("Reactive-serial")] // process-static counters, like GlobalVersion
public class ReactiveCountersTests
{
    [Fact]
    public void Writes_counts_committed_writes_only()
    {
        var s = new Signal<int>(0);
        long before = Reactive.Writes;

        s.Value = 0; // equal → not a write
        Assert.Equal(expected: before, actual: Reactive.Writes);

        s.Value = 1;
        Assert.Equal(expected: before + 1, actual: Reactive.Writes);
    }

    [Fact]
    public void Runs_counts_reaction_bodies_and_stops_when_nothing_changes()
    {
        var s = new Signal<int>(0);
        var doubled = Computed.From(() => s.Value * 2);
        using var effect = new Effect(() => _ = doubled.Value);

        long before = Reactive.Runs;
        s.Value = 1; // computed recomputes + effect re-runs
        long afterWrite = Reactive.Runs;
        Assert.Equal(expected: 2, actual: afterWrite - before);

        // Idle graph: reads are cached, nothing re-derives. This is the assertion that makes the
        // counter readable as "churn" at all.
        _ = doubled.Value;
        _ = doubled.Value;
        Assert.Equal(expected: afterWrite, actual: Reactive.Runs);

        // A write whose value is unchanged downstream settles the intermediate without waking the
        // effect — one run, not two.
        var unchanged = Computed.From(() => s.Value > 0);
        using var gate = new Effect(() => _ = unchanged.Value);
        long beforeSecond = Reactive.Runs;
        s.Value = 2; // unchanged stays true → its effect must not re-run
        Assert.Equal(
            expected: 3,
            actual: Reactive.Runs - beforeSecond
        ); // doubled, unchanged, doubled's effect
    }

    [Fact]
    public void Attribution_names_the_body_that_ran()
    {
        var s = new Signal<int>(0);
        Reactive.ResetReactionStats();
        Reactive.TrackReactions = true;
        try
        {
            var doubled = Computed.From(() => s.Value * 2);
            using var effect = new Effect(() => _ = doubled.Value);
            s.Value = 1;

            var hottest = Reactive.HottestReactions();

            // Both bodies are lambdas declared in this test method, so the display class must be
            // unwrapped back to the test — an unnamed "<>c.<M>b__0" row helps nobody.
            Assert.All(
                collection: hottest,
                action: h =>
                    Assert.StartsWith(
                        expectedStartString: nameof(ReactiveCountersTests) + "." +
                                             nameof(Attribution_names_the_body_that_ran),
                        actualString: h.Label
                    )
            );
            Assert.Equal(
                expected: Reactive.HottestReactions().Sum(h => h.Runs),
                actual: hottest.Sum(h => h.Runs)
            );
            Assert.True(
                condition: hottest[0].Runs >= hottest[^1].Runs,
                userMessage: "not sorted by run count"
            );
        }
        finally
        {
            Reactive.TrackReactions = false;
            Reactive.ResetReactionStats();
        }
    }

    [Fact]
    public void Attribution_is_off_by_default_and_records_nothing()
    {
        Reactive.ResetReactionStats();
        var s = new Signal<int>(0);
        using var effect = new Effect(() => _ = s.Value);
        s.Value = 1;

        Assert.False(Reactive.TrackReactions);
        Assert.Empty(Reactive.HottestReactions());
    }

    [Fact]
    public void PendingDeferred_reports_the_last_drains_backlog()
    {
        var s = new Signal<int>(0);
        int runs = 0;
        using var e = new Effect(
            body: () =>
            {
                _ = s.Value;
                runs++;
            },
            affinity: EffectAffinity.Deferred
        );

        Reactive.DrainDeferred(); // clear the construction-time queue
        Assert.Equal(expected: 0, actual: Reactive.PendingDeferred);

        int before = runs;
        s.Value = 1; // parks the effect instead of running it here
        Assert.Equal(expected: before, actual: runs);

        Reactive.DrainDeferred();
        Assert.Equal(expected: 1, actual: Reactive.PendingDeferred);
        Assert.Equal(expected: before + 1, actual: runs);
    }

    [Fact]
    public void Watch_counts_its_own_rebuilds_for_the_inspector()
    {
        // Per-widget counters are opt-in (the inspector enables them when it opens); the gate is
        // what keeps idle apps from dirtying every widget's cache line for unread data.
        bool was = WidgetDebug.CountersEnabled;
        WidgetDebug.CountersEnabled = true;
        try
        {
            RunWatchCounterScenario();
        }
        finally
        {
            WidgetDebug.CountersEnabled = was;
        }
    }

    private static void RunWatchCounterScenario()
    {
        var count = new Signal<int>(0);
        var watch = new Watch(() => new Label($"{count.Value}"));

        watch.Attach(owner: null!, parent: null);
        watch.Measure(Constraints.Tight(width: 100f, height: 20f));
        Assert.Equal(
            expected: 0,
            actual: watch.RebuildCount
        ); // first materialisation is not a rebuild

        long before = Watch.Rebuilds;
        count.Value = 1;
        watch.Measure(Constraints.Tight(width: 100f, height: 20f));

        Assert.Equal(expected: 1, actual: watch.RebuildCount);
        Assert.Equal(expected: before + 1, actual: Watch.Rebuilds);
    }
}
