using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="ScrollView.EnsureVisible" /> drives the editor's "scroll the hierarchy to the
///     selected node" behaviour. It resolves a content-space band into a scroll target (eased via a
///     ticker), so these assert on <see cref="ScrollView.TargetOffsetY" /> — where the scroll is
///     heading — independent of the per-frame easing. The reveal is applied in Layout (after the
///     scroll extent is known), so each case re-lays-out before asserting.
/// </summary>
public class ScrollEnsureVisibleTests
{
    // 200px-wide, 2000px-tall content inside a 400px-tall viewport → 1600px of scroll extent.
    private static ScrollView FreshScroll()
    {
        var scroll =
            new ScrollView(new SizedBox(width: 200, height: 2000)) { ScrollVertical = true };
        scroll.Measure(
            new Constraints(
                minWidth: 0,
                maxWidth: 200,
                minHeight: 0,
                maxHeight: 400
            )
        );
        scroll.Layout(Offset.Zero);
        return scroll;
    }

    [Fact]
    public void BandBelowViewport_ScrollsDownToRevealBottomEdge()
    {
        var scroll = FreshScroll();
        Assert.Equal(expected: 0f, actual: scroll.TargetOffsetY, precision: 1);

        // Band at 1500..1526 is below the 0..400 view → scroll so its bottom + margin sits at the edge.
        scroll.EnsureVisible(top: 1500f, height: 26f);
        scroll.Layout(Offset.Zero);

        // bottom = 1500 + 26 + 8 = 1534; target = bottom − viewHeight = 1534 − 400 = 1134.
        Assert.Equal(expected: 1134f, actual: scroll.TargetOffsetY, precision: 1);
    }

    [Fact]
    public void BandAboveViewport_ScrollsUpToRevealTopEdge()
    {
        var scroll = FreshScroll();
        scroll.OffsetY = 1000f; // scrolled down past the band
        scroll.Layout(Offset.Zero);

        scroll.EnsureVisible(top: 100f, height: 26f);
        scroll.Layout(Offset.Zero);

        // top − margin = 92, which is above the current 1000 → target snaps to 92.
        Assert.Equal(expected: 92f, actual: scroll.TargetOffsetY, precision: 1);
    }

    [Fact]
    public void BandAlreadyVisible_DoesNotScroll()
    {
        var scroll = FreshScroll();

        // 100..126 is fully inside the 0..400 view (even with margin) → no movement.
        scroll.EnsureVisible(top: 100f, height: 26f);
        scroll.Layout(Offset.Zero);

        Assert.Equal(expected: 0f, actual: scroll.TargetOffsetY, precision: 1);
    }

    [Fact]
    public void RevealTargetIsClampedToScrollExtent()
    {
        var scroll = FreshScroll();

        // Asking to reveal beyond the content end must clamp to the max scroll (1600), never overshoot.
        scroll.EnsureVisible(top: 5000f, height: 26f);
        scroll.Layout(Offset.Zero);

        Assert.Equal(expected: 1600f, actual: scroll.TargetOffsetY, precision: 1);
    }
}
