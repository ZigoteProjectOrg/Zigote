using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Touchscreen input building blocks: 1:1 drag-to-scroll with fling on the scrollables,
///     press-cancel semantics (a claimed touch drag must not commit the pressed widget's tap),
///     long-press detection, and the pooled <see cref="TouchMoveEvent" /> decode path. The
///     App-level finger routing itself needs a live window (SmokeTest territory); these pin the
///     widget/scroller contracts it composes.
/// </summary>
[Collection(
    "Ticker"
)] // static Ticker.Active is shared; AdvanceAll in one class ticks another class's widgets
public class TouchInputTests
{
    // 200px-wide, 2000px-tall content inside a 400px-tall viewport → 1600px of scroll extent.
    private static ScrollView FreshScroll()
    {
        var scroll = new ScrollView(new SizedBox(200, 2000)) { ScrollVertical = true };
        scroll.Measure(
            new Constraints(
                0,
                200,
                0,
                400
            )
        );
        scroll.Layout(Offset.Zero);
        return scroll;
    }

    [Fact]
    public void ScrollView_TouchScroll_TracksFingerOneToOne()
    {
        var scroll = FreshScroll();

        // Finger moved 120px up (dy = -120): content follows the finger → offset grows 120,
        // with no wheel-tick speed multiplier and no easing lag.
        scroll.OnTouchScroll(0f, -120f);
        Assert.Equal(120f, scroll.OffsetY, 1);

        // Finger back down 20px returns 20px of content.
        scroll.OnTouchScroll(0f, 20f);
        Assert.Equal(100f, scroll.OffsetY, 1);
    }

    [Fact]
    public void ScrollView_TouchScroll_AtEdge_BubblesToScrollParent()
    {
        var outer = FreshScroll();
        var inner = FreshScroll();
        inner.ScrollParent = outer;

        // Dragging DOWN at the top edge (dy > 0 cannot move offset below 0) must bubble the
        // same finger delta to the ancestor scrollable, like wheel scrolling does.
        outer.OnTouchScroll(0f, -50f); // give the outer room to scroll back
        inner.OnTouchScroll(0f, 30f);
        Assert.Equal(0f, inner.OffsetY, 1);
        Assert.Equal(20f, outer.OffsetY, 1);
    }

    [Fact]
    public void ScrollView_CanTouchScroll_ReflectsAxisAndOverflow()
    {
        var scroll = FreshScroll();
        Assert.True(scroll.CanTouchScroll(true));
        // No horizontal overflow (and ScrollHorizontal is off by default in this setup).
        Assert.False(scroll.CanTouchScroll(false));

        // Content that fits has nothing to scroll — the drag must fall through to the
        // pressed widget instead of being eaten by a scrollable with zero extent.
        var fits = new ScrollView(new SizedBox(200, 300)) { ScrollVertical = true };
        fits.Measure(
            new Constraints(
                0,
                200,
                0,
                400
            )
        );
        fits.Layout(Offset.Zero);
        Assert.False(fits.CanTouchScroll(true));
    }

    [Fact]
    public void ScrollView_TouchFling_GlidesAndStopsWithinExtent()
    {
        var scroll = FreshScroll();

        // Finger lifted mid-drag moving up at 1200 px/s → content keeps gliding forward.
        scroll.OnTouchFling(0f, -1200f);
        Ticker.AdvanceAll(0.016f);
        var afterOneFrame = scroll.OffsetY;
        Assert.True(afterOneFrame > 0f, "fling should start moving the content");

        // Run the decay out (parallel test classes may advance tickers too — assert on the
        // settled invariants, not exact frames).
        for (var i = 0; i < 400; i++) Ticker.AdvanceAll(0.016f);
        Assert.True(scroll.OffsetY >= afterOneFrame);
        Assert.InRange(scroll.OffsetY, 0f, 1600f);
    }

    [Fact]
    public void ScrollView_TouchFling_BelowMinVelocity_Bubbles()
    {
        var outer = FreshScroll();
        var inner = FreshScroll();
        inner.ScrollParent = outer;

        // A crawl-speed lift must not fling — and per the bubbling contract the un-started
        // fling is offered to the ancestor, which can't use it either.
        inner.OnTouchFling(0f, -10f);
        Ticker.AdvanceAll(0.5f);
        Assert.Equal(0f, inner.OffsetY, 1);
        Assert.Equal(0f, outer.OffsetY, 1);
    }

    [Fact]
    public void Pressable_PointerCancel_ReleasesPressWithoutFiring()
    {
        var fired = 0;
        var p = new Pressable {
            Child = new SizedBox(100, 40),
            OnPressed = () => fired++,
        };
        p.Measure(Constraints.Tight(100, 40));
        p.Layout(Offset.Zero);

        // Touch down arms the press; a scroll gesture then claims the pointer (cancel).
        p.OnPointerDown(new Offset(50, 20));
        Assert.True(p.Pressed);
        p.OnPointerCancel();
        Assert.False(p.Pressed);

        // Even if an up still arrives inside bounds afterwards, the tap must not fire.
        p.OnPointerUp(new Offset(50, 20));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void GestureDetector_LongPress_FiresCallbackAndSuppressesTap()
    {
        var taps = 0;
        var longPresses = 0;
        var gd = new GestureDetector(
            new SizedBox(100, 40),
            () => taps++,
            onLongPress: () => longPresses++
        );
        gd.Measure(Constraints.Tight(100, 40));
        gd.Layout(Offset.Zero);

        var point = new Offset(50, 20);
        gd.OnPointerDown(point);
        gd.OnLongPress(point); // the App fires this after the hold threshold
        Assert.Equal(1, longPresses);

        // The long-press consumed the gesture — the eventual lift is not also a tap.
        gd.OnPointerUp(point);
        Assert.Equal(0, taps);
    }

    [Fact]
    public void GestureDetector_PointerCancel_SuppressesTap()
    {
        var taps = 0;
        var gd = new GestureDetector(new SizedBox(100, 40), () => taps++);
        gd.Measure(Constraints.Tight(100, 40));
        gd.Layout(Offset.Zero);

        gd.OnPointerDown(new Offset(50, 20));
        gd.OnPointerCancel();
        gd.OnPointerUp(new Offset(50, 20));
        Assert.Equal(0, taps);
    }

    [Fact]
    public void GestureDetector_WithoutLongPressCallback_TapStillFiresAfterHold()
    {
        // Press-hold-release on a plain tappable: the default long-press mapping
        // (context menu via OnRightClick) is a no-op here, so the lift still counts as a tap.
        var taps = 0;
        var gd = new GestureDetector(new SizedBox(100, 40), () => taps++);
        gd.Measure(Constraints.Tight(100, 40));
        gd.Layout(Offset.Zero);

        var point = new Offset(50, 20);
        gd.OnPointerDown(point);
        gd.OnLongPress(point);
        gd.OnPointerUp(point);
        Assert.Equal(1, taps);
    }

    [Fact]
    public void EventPool_TouchMove_ReusesInstancesAcrossPolls()
    {
        var pool = new EventPool();
        var a = pool.RentTouchMove(
            1f,
            2f,
            0,
            1f,
            0
        );
        var b = pool.RentTouchMove(
            3f,
            4f,
            0,
            1f,
            0
        );
        Assert.NotSame(a, b); // two moves within one poll keep distinct coordinates

        pool.Reset();
        var c = pool.RentTouchMove(
            5f,
            6f,
            1,
            0.5f,
            7
        );
        Assert.Same(a, c); // next poll reuses the first instance…
        Assert.Equal(5f, c.X); // …fully overwritten
        Assert.Equal(6f, c.Y);
        Assert.Equal(1, c.Finger);
        Assert.Equal(0.5f, c.Pressure);
        Assert.Equal(7u, c.WindowId);
    }
}