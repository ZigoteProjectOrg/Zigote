using Xunit;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Tests;

/// <summary>
///     The two properties a clock-driven view leans on to stay cheap: a projection does not wake its
///     observers when the fact it projects is unchanged, and a Watch that hands back the widget it
///     handed back last time swaps nothing (a swap marks layout, which costs a full-frame repaint).
/// </summary>
// Exact rebuild counts over the process-wide reactive graph: another collection writing signals in
// parallel perturbs them, so this joins the serialized reactive tests.
[Collection("Reactive-serial")]
public class WatchTickCostTests
{
    [Fact]
    public void Projection_DoesNotWake_OnUnrelatedTicks()
    {
        var state = new Signal<Tick>(new Tick(Track: "a", Position: 0f));
        var track = Computed.From(() => state.Value.Track);

        int builds = 0;
        var watch = new Watch(() =>
            {
                builds++;
                return new Label(track.Value);
            }
        );
        watch.Measure(
            Constraints.Tight(width: 100f, height: 20f)
        ); // starts the watch (no App needed)
        Assert.Equal(expected: 1, actual: builds);

        for (int i = 1; i <= 5; i++) state.Value = new Tick(Track: "a", Position: i);
        Assert.Equal(
            expected: 1,
            actual: builds
        ); // the position moved five times; the track did not

        state.Value = new Tick(Track: "b", Position: 5f);
        Assert.Equal(expected: 2, actual: builds);
    }

    [Fact]
    public void SameWidgetBack_IsNotASwap()
    {
        var state = new Signal<float>(0f);
        var label = new Label("0:00");

        int builds = 0;
        var watch = new Watch(() =>
            {
                builds++;
                label.Text = $"{(int)state.Value}"; // Label.Text is equality-guarded
                return label;
            }
        );
        watch.Measure(Constraints.Tight(width: 100f, height: 20f));

        state.Value = 1f;
        state.Value = 2f;

        Assert.Equal(
            expected: 3,
            actual: builds
        ); // the builder runs on every tick — it has to push the value
        Assert.Equal(expected: 0, actual: watch.RebuildCount); // …and never swaps its subtree
    }

    private sealed record Tick(string Track, float Position);
}
