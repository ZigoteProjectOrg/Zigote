using Xunit;
using Zigote.Core;
using Zigote.UI.BottomSheets;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     The load-bearing halves of a flexible bottom sheet: the extent → geometry mapping, the drag
///     surface that moves it, the scrim that dismisses it, and the hand-off between resizing the sheet
///     and scrolling its content — the one behaviour that makes a sheet feel like a single surface.
/// </summary>
// The sheet drives its position through a Signal, so it shares the reactive graph the
// serialized reactive tests assert global counters on.
[Collection("Reactive-serial")]
public class FlexibleBottomSheetTests
{
    private const float W = 400f, H = 600f;

    private static FlexibleBottomSheet Laid(
        out BottomSheetController controller,
        out List<object?> closes,
        Widget? content = null,
        float min = 0.25f,
        float init = 0.5f,
        float max = 1f,
        IReadOnlyList<float>? anchors = null,
        bool collapsible = true)
    {
        controller = new BottomSheetController(
            min,
            init,
            max,
            anchors,
            collapsible
        );
        var closed = new List<object?>();
        closes = closed;
        controller.OnClose = r => closed.Add(r);

        var sheet = new FlexibleBottomSheet(content ?? new SizedBox(W, 1000f), controller);
        sheet.Measure(Constraints.Tight(W, H));
        sheet.Layout(Offset.Zero);
        return sheet;
    }

    /// <summary>Top edge of the card at the current extent.</summary>
    private static float CardTop(BottomSheetController c)
    {
        return H - c.PixelHeight;
    }

    [Fact]
    public void ExtentDrivesTheCardHeight()
    {
        var sheet = Laid(out var c, out _);

        Assert.Equal(H, c.AvailableHeight);
        Assert.Equal(300f, c.PixelHeight);

        // Inside the card: the sheet's own content, not the sheet acting as a barrier.
        Assert.NotSame(sheet, sheet.HitTest(new Offset(200f, CardTop(c) + 50f)));
        // Above it: the modal scrim, which is the sheet itself.
        Assert.Same(sheet, sheet.HitTest(new Offset(200f, 40f)));
    }

    [Fact]
    public void DraggingTheHandleResizesTheSheet()
    {
        var sheet = Laid(out var c, out _);
        var handle = Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(200f, CardTop(c) + 8f)));

        handle.OnPointerDown(new Offset(200f, 300f));
        handle.OnPointerMove(new Offset(200f, 240f)); // 60 px up = one tenth of the height

        Assert.Equal(0.6f, c.Value, 3);
    }

    [Fact]
    public void HoveringTheHandleDoesNotDragIt()
    {
        var sheet = Laid(out var c, out _);
        var handle = Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(200f, CardTop(c) + 8f)));

        // With nothing captured the app routes moves to the hovered widget, so a bare move must be
        // inert — the pill followed the cursor without a press before this guard.
        handle.OnPointerMove(new Offset(200f, 240f));
        handle.OnPointerMove(new Offset(200f, 180f));

        Assert.Equal(0.5f, c.Value, 3);
    }

    [Fact]
    public void DraggingBelowTheMinimumDismissesACollapsibleSheet()
    {
        var sheet = Laid(out var c, out var closes);
        var handle = Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(200f, CardTop(c) + 8f)));

        handle.OnPointerDown(new Offset(200f, 300f));
        handle.OnPointerMove(new Offset(200f, 500f)); // 200 px down: below MinExtent
        Assert.True(c.Value < c.MinExtent);

        handle.OnPointerUp(new Offset(200f, 500f));
        Assert.Single(closes);
    }

    [Fact]
    public void DraggingBelowTheMinimumSnapsBackWhenNotCollapsible()
    {
        var sheet = Laid(out var c, out var closes, collapsible: false);
        var handle = Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(200f, CardTop(c) + 8f)));

        handle.OnPointerDown(new Offset(200f, 300f));
        handle.OnPointerMove(new Offset(200f, 500f));
        handle.OnPointerUp(new Offset(200f, 500f));

        Assert.Empty(closes);
        Assert.Equal(c.MinExtent, c.Value, 3);
    }

    [Fact]
    public void TappingTheScrimDismisses()
    {
        var sheet = Laid(out _, out var closes);

        sheet.OnPointerUp(new Offset(200f, 40f));
        Assert.Single(closes);
    }

    [Fact]
    public void ANonDismissibleScrimSwallowsTheTap()
    {
        var sheet = Laid(out _, out var closes);
        sheet.IsDismissible = false;

        sheet.OnPointerUp(new Offset(200f, 40f));
        Assert.Empty(closes);
    }

    [Fact]
    public void AParkedSheetIsTransparentToHitTesting()
    {
        var sheet = Laid(
            out var c,
            out _,
            min: 0f,
            init: 0f
        );

        // Nothing on screen: a persistent sheet sitting closed over a page must not eat its clicks.
        Assert.Null(sheet.HitTest(new Offset(200f, 300f)));
    }

    [Fact]
    public void ReleasedDragSettlesOnTheNearestAnchor()
    {
        var sheet = Laid(
            out var c,
            out _,
            anchors: [0.3f, 0.6f, 0.9f]
        );
        var handle = Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(200f, CardTop(c) + 8f)));

        // The settle target, not the animated value: ticking the snap here would mean driving the
        // process-wide ticker list, which races with every other test's animations.
        var settled = new List<float>();
        c.Settling += t => settled.Add(t);

        handle.OnPointerDown(new Offset(200f, 300f));
        handle.OnPointerMove(new Offset(200f, 290f)); // 10 px up → 0.5167, nearest anchor is 0.6
        handle.OnPointerUp(new Offset(200f, 290f));

        Assert.Equal(0.6f, Assert.Single(settled), 3);
    }

    [Fact]
    public void TheContentDragGrowsTheSheetFirstAndScrollsAfterwards()
    {
        BottomSheetController? c = null;
        var scroll = new SheetScrollView(
            c = new BottomSheetController(0.25f, 0.5f),
            new SizedBox(W, 2000f)
        );
        var sheet = new FlexibleBottomSheet(scroll, c);
        sheet.Measure(Constraints.Tight(W, H));
        sheet.Layout(Offset.Zero);

        // Not yet fully expanded: an upward drag is the sheet growing, and the list stays put.
        scroll.OnTouchScroll(0f, -60f);
        Assert.Equal(0.6f, c.Value, 3);
        Assert.Equal(0f, scroll.OffsetY, 3);

        // Fully expanded: the same drag scrolls the content.
        c.JumpTo(1f);
        sheet.Measure(Constraints.Tight(W, H));
        sheet.Layout(Offset.Zero);
        scroll.OnTouchScroll(0f, -60f);
        Assert.Equal(1f, c.Value, 3);
        Assert.Equal(60f, scroll.OffsetY, 3);

        // Dragging back down scrolls the content to its top before the sheet shrinks…
        scroll.OnTouchScroll(0f, 60f);
        Assert.Equal(0f, scroll.OffsetY, 3);
        Assert.Equal(1f, c.Value, 3);

        // …and only then shrinks it.
        scroll.OnTouchScroll(0f, 60f);
        Assert.Equal(0.9f, c.Value, 3);
    }

    [Fact]
    public void TheContentDragNeverStrandsTheSheetBelowItsMinimum()
    {
        var c = new BottomSheetController(0.25f, 0.3f);
        var scroll = new SheetScrollView(c, new SizedBox(W, 2000f));
        var sheet = new FlexibleBottomSheet(scroll, c);
        sheet.Measure(Constraints.Tight(W, H));
        sheet.Layout(Offset.Zero);

        // A scroll target that is not the pressed widget never sees the release, so the body may
        // shrink the sheet only down to its minimum — collapsing belongs to the handle.
        scroll.OnTouchScroll(0f, 200f);
        Assert.Equal(c.MinExtent, c.Value, 3);
    }
}