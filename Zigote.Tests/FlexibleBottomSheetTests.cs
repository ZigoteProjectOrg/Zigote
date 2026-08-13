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
            minExtent: min,
            initExtent: init,
            maxExtent: max,
            anchors: anchors,
            isCollapsible: collapsible
        );
        var closed = new List<object?>();
        closes = closed;
        controller.OnClose = r => closed.Add(r);

        var sheet = new FlexibleBottomSheet(
            content: content ?? new SizedBox(width: W, height: 1000f),
            controller: controller
        );
        sheet.Measure(Constraints.Tight(width: W, height: H));
        sheet.Layout(Offset.Zero);
        return sheet;
    }

    /// <summary>Top edge of the card at the current extent.</summary>
    private static float CardTop(BottomSheetController c) => H - c.PixelHeight;

    [Fact]
    public void ExtentDrivesTheCardHeight()
    {
        var sheet = Laid(controller: out var c, closes: out _);

        Assert.Equal(expected: H, actual: c.AvailableHeight);
        Assert.Equal(expected: 300f, actual: c.PixelHeight);

        // Inside the card: the sheet's own content, not the sheet acting as a barrier.
        Assert.NotSame(
            expected: sheet,
            actual: sheet.HitTest(new Offset(x: 200f, y: CardTop(c) + 50f))
        );
        // Above it: the modal scrim, which is the sheet itself.
        Assert.Same(expected: sheet, actual: sheet.HitTest(new Offset(x: 200f, y: 40f)));
    }

    [Fact]
    public void DraggingTheHandleResizesTheSheet()
    {
        var sheet = Laid(controller: out var c, closes: out _);
        var handle =
            Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(x: 200f, y: CardTop(c) + 8f)));

        handle.OnPointerDown(new Offset(x: 200f, y: 300f));
        handle.OnPointerMove(new Offset(x: 200f, y: 240f)); // 60 px up = one tenth of the height

        Assert.Equal(expected: 0.6f, actual: c.Value, precision: 3);
    }

    [Fact]
    public void HoveringTheHandleDoesNotDragIt()
    {
        var sheet = Laid(controller: out var c, closes: out _);
        var handle =
            Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(x: 200f, y: CardTop(c) + 8f)));

        // With nothing captured the app routes moves to the hovered widget, so a bare move must be
        // inert — the pill followed the cursor without a press before this guard.
        handle.OnPointerMove(new Offset(x: 200f, y: 240f));
        handle.OnPointerMove(new Offset(x: 200f, y: 180f));

        Assert.Equal(expected: 0.5f, actual: c.Value, precision: 3);
    }

    [Fact]
    public void DraggingBelowTheMinimumDismissesACollapsibleSheet()
    {
        var sheet = Laid(controller: out var c, closes: out var closes);
        var handle =
            Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(x: 200f, y: CardTop(c) + 8f)));

        handle.OnPointerDown(new Offset(x: 200f, y: 300f));
        handle.OnPointerMove(new Offset(x: 200f, y: 500f)); // 200 px down: below MinExtent
        Assert.True(c.Value < c.MinExtent);

        handle.OnPointerUp(new Offset(x: 200f, y: 500f));
        Assert.Single(closes);
    }

    [Fact]
    public void DraggingBelowTheMinimumSnapsBackWhenNotCollapsible()
    {
        var sheet = Laid(controller: out var c, closes: out var closes, collapsible: false);
        var handle =
            Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(x: 200f, y: CardTop(c) + 8f)));

        handle.OnPointerDown(new Offset(x: 200f, y: 300f));
        handle.OnPointerMove(new Offset(x: 200f, y: 500f));
        handle.OnPointerUp(new Offset(x: 200f, y: 500f));

        Assert.Empty(closes);
        Assert.Equal(expected: c.MinExtent, actual: c.Value, precision: 3);
    }

    [Fact]
    public void TappingTheScrimDismisses()
    {
        var sheet = Laid(controller: out _, closes: out var closes);

        sheet.OnPointerUp(new Offset(x: 200f, y: 40f));
        Assert.Single(closes);
    }

    [Fact]
    public void ANonDismissibleScrimSwallowsTheTap()
    {
        var sheet = Laid(controller: out _, closes: out var closes);
        sheet.IsDismissible = false;

        sheet.OnPointerUp(new Offset(x: 200f, y: 40f));
        Assert.Empty(closes);
    }

    [Fact]
    public void AParkedSheetIsTransparentToHitTesting()
    {
        var sheet = Laid(
            controller: out var c,
            closes: out _,
            min: 0f,
            init: 0f
        );

        // Nothing on screen: a persistent sheet sitting closed over a page must not eat its clicks.
        Assert.Null(sheet.HitTest(new Offset(x: 200f, y: 300f)));
    }

    [Fact]
    public void ReleasedDragSettlesOnTheNearestAnchor()
    {
        var sheet = Laid(
            controller: out var c,
            closes: out _,
            anchors: [0.3f, 0.6f, 0.9f]
        );
        var handle =
            Assert.IsType<SheetDragArea>(sheet.HitTest(new Offset(x: 200f, y: CardTop(c) + 8f)));

        // The settle target, not the animated value: ticking the snap here would mean driving the
        // process-wide ticker list, which races with every other test's animations.
        var settled = new List<float>();
        c.Settling += t => settled.Add(t);

        handle.OnPointerDown(new Offset(x: 200f, y: 300f));
        handle.OnPointerMove(
            new Offset(x: 200f, y: 290f)
        ); // 10 px up → 0.5167, nearest anchor is 0.6
        handle.OnPointerUp(new Offset(x: 200f, y: 290f));

        Assert.Equal(expected: 0.6f, actual: Assert.Single(settled), precision: 3);
    }

    [Fact]
    public void TheContentDragGrowsTheSheetFirstAndScrollsAfterwards()
    {
        BottomSheetController? c = null;
        var scroll = new SheetScrollView(
            sheet: c = new BottomSheetController(minExtent: 0.25f, initExtent: 0.5f),
            child: new SizedBox(width: W, height: 2000f)
        );
        var sheet = new FlexibleBottomSheet(content: scroll, controller: c);
        sheet.Measure(Constraints.Tight(width: W, height: H));
        sheet.Layout(Offset.Zero);

        // Not yet fully expanded: an upward drag is the sheet growing, and the list stays put.
        scroll.OnTouchScroll(dx: 0f, dy: -60f);
        Assert.Equal(expected: 0.6f, actual: c.Value, precision: 3);
        Assert.Equal(expected: 0f, actual: scroll.OffsetY, precision: 3);

        // Fully expanded: the same drag scrolls the content.
        c.JumpTo(1f);
        sheet.Measure(Constraints.Tight(width: W, height: H));
        sheet.Layout(Offset.Zero);
        scroll.OnTouchScroll(dx: 0f, dy: -60f);
        Assert.Equal(expected: 1f, actual: c.Value, precision: 3);
        Assert.Equal(expected: 60f, actual: scroll.OffsetY, precision: 3);

        // Dragging back down scrolls the content to its top before the sheet shrinks…
        scroll.OnTouchScroll(dx: 0f, dy: 60f);
        Assert.Equal(expected: 0f, actual: scroll.OffsetY, precision: 3);
        Assert.Equal(expected: 1f, actual: c.Value, precision: 3);

        // …and only then shrinks it.
        scroll.OnTouchScroll(dx: 0f, dy: 60f);
        Assert.Equal(expected: 0.9f, actual: c.Value, precision: 3);
    }

    [Fact]
    public void TheContentDragNeverStrandsTheSheetBelowItsMinimum()
    {
        var c = new BottomSheetController(minExtent: 0.25f, initExtent: 0.3f);
        var scroll = new SheetScrollView(sheet: c, child: new SizedBox(width: W, height: 2000f));
        var sheet = new FlexibleBottomSheet(content: scroll, controller: c);
        sheet.Measure(Constraints.Tight(width: W, height: H));
        sheet.Layout(Offset.Zero);

        // A scroll target that is not the pressed widget never sees the release, so the body may
        // shrink the sheet only down to its minimum — collapsing belongs to the handle.
        scroll.OnTouchScroll(dx: 0f, dy: 200f);
        Assert.Equal(expected: c.MinExtent, actual: c.Value, precision: 3);
    }
}
