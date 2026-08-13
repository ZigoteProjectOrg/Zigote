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
    public void ScrollView_TouchScroll_TracksFingerOneToOne()
    {
        var scroll = FreshScroll();

        // Finger moved 120px up (dy = -120): content follows the finger → offset grows 120,
        // with no wheel-tick speed multiplier and no easing lag.
        scroll.OnTouchScroll(dx: 0f, dy: -120f);
        Assert.Equal(expected: 120f, actual: scroll.OffsetY, precision: 1);

        // Finger back down 20px returns 20px of content.
        scroll.OnTouchScroll(dx: 0f, dy: 20f);
        Assert.Equal(expected: 100f, actual: scroll.OffsetY, precision: 1);
    }

    [Fact]
    public void ScrollView_TouchScroll_AtEdge_BubblesToScrollParent()
    {
        var outer = FreshScroll();
        var inner = FreshScroll();
        inner.ScrollParent = outer;

        // Dragging DOWN at the top edge (dy > 0 cannot move offset below 0) must bubble the
        // same finger delta to the ancestor scrollable, like wheel scrolling does.
        outer.OnTouchScroll(dx: 0f, dy: -50f); // give the outer room to scroll back
        inner.OnTouchScroll(dx: 0f, dy: 30f);
        Assert.Equal(expected: 0f, actual: inner.OffsetY, precision: 1);
        Assert.Equal(expected: 20f, actual: outer.OffsetY, precision: 1);
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
        var fits =
            new ScrollView(new SizedBox(width: 200, height: 300)) { ScrollVertical = true };
        fits.Measure(
            new Constraints(
                minWidth: 0,
                maxWidth: 200,
                minHeight: 0,
                maxHeight: 400
            )
        );
        fits.Layout(Offset.Zero);
        Assert.False(fits.CanTouchScroll(true));
    }

    // ── Scrub controls outrank the scroller they sit in ────────────────────────
    //
    // A touch drag is arbitrated once, when it passes the slop: the App walks the pressed widget's
    // scroll chain and gives the gesture to the first widget that claims it. A control being
    // scrubbed claims BOTH axes (Widget.CanTouchDrag) and is asked before any scroller, so the page
    // cannot take the drag away — the bug this pins is a fader inside a scrolling page that could
    // never be moved, and a horizontal slider that lost the gesture whenever the finger settled
    // downward before setting off sideways.

    private static Slider PressedSlider(float value = 0.5f)
    {
        var slider = new Slider(value);
        slider.Measure(Constraints.Tight(width: 200, height: 44));
        slider.Layout(Offset.Zero);
        slider.OnPointerDown(new Offset(x: 100, y: 22));
        return slider;
    }

    [Fact]
    public void Slider_BeingScrubbed_ClaimsBothAxesFromTheScroller()
    {
        var slider = PressedSlider();

        // Horizontal is the scrub axis; vertical is the one a page would otherwise steal.
        Assert.True(slider.CanTouchDrag(false));
        Assert.True(slider.CanTouchDrag(true));

        // The lift ends the claim: the next drag over the control is the page's again.
        slider.OnPointerUp(new Offset(x: 100, y: 22));
        Assert.False(slider.CanTouchDrag(false));
        Assert.False(slider.CanTouchDrag(true));
    }

    [Fact]
    public void Slider_NotPressed_ClaimsNothing()
    {
        var slider = new Slider(0.5f);
        slider.Measure(Constraints.Tight(width: 200, height: 44));
        slider.Layout(Offset.Zero);

        // No press, no claim — a finger that merely passes over the control while the page
        // scrolls must not park the gesture on it.
        Assert.False(slider.CanTouchDrag(true));
        Assert.False(slider.CanTouchDrag(false));

        // A disabled control starts no scrub either, so its row stays a scroll surface.
        var off = new Slider(0.5f) { Enabled = false };
        off.Measure(Constraints.Tight(width: 200, height: 44));
        off.Layout(Offset.Zero);
        off.OnPointerDown(new Offset(x: 100, y: 22));
        Assert.False(off.CanTouchDrag(true));
        Assert.False(off.CanTouchDrag(false));
    }

    [Fact]
    public void Slider_CancelledByAnotherGesture_ReleasesItsClaim()
    {
        var slider = PressedSlider();
        Assert.True(slider.CanTouchDrag(true));

        // A pinch (or any app-level takeover) cancels the press; the claim must go with it,
        // otherwise the control keeps the gesture pinned after it stopped tracking the finger.
        slider.OnPointerCancel();
        Assert.False(slider.CanTouchDrag(true));
        Assert.False(slider.CanTouchDrag(false));
    }

    [Fact]
    public void ScrollView_ClaimsNothingAsADrag_SoItStaysAScroller()
    {
        // The scroller answers CanTouchScroll, never CanTouchDrag: the two are asked in that
        // order and a scroller claiming both would make the distinction meaningless.
        var scroll = FreshScroll();
        Assert.False(scroll.CanTouchDrag(true));
        Assert.False(scroll.CanTouchDrag(false));
    }

    [Fact]
    public void ScrollView_TouchFling_GlidesAndStopsWithinExtent()
    {
        var scroll = FreshScroll();

        // Finger lifted mid-drag moving up at 1200 px/s → content keeps gliding forward.
        scroll.OnTouchFling(velocityX: 0f, velocityY: -1200f);
        Ticker.AdvanceAll(0.016f);
        float afterOneFrame = scroll.OffsetY;
        Assert.True(
            condition: afterOneFrame > 0f,
            userMessage: "fling should start moving the content"
        );

        // Run the decay out (parallel test classes may advance tickers too — assert on the
        // settled invariants, not exact frames).
        for (int i = 0; i < 400; i++) Ticker.AdvanceAll(0.016f);
        Assert.True(scroll.OffsetY >= afterOneFrame);
        Assert.InRange(actual: scroll.OffsetY, low: 0f, high: 1600f);
    }

    [Fact]
    public void ScrollView_TouchFling_BelowMinVelocity_Bubbles()
    {
        var outer = FreshScroll();
        var inner = FreshScroll();
        inner.ScrollParent = outer;

        // A crawl-speed lift must not fling — and per the bubbling contract the un-started
        // fling is offered to the ancestor, which can't use it either.
        inner.OnTouchFling(velocityX: 0f, velocityY: -10f);
        Ticker.AdvanceAll(0.5f);
        Assert.Equal(expected: 0f, actual: inner.OffsetY, precision: 1);
        Assert.Equal(expected: 0f, actual: outer.OffsetY, precision: 1);
    }

    [Fact]
    public void Pressable_PointerCancel_ReleasesPressWithoutFiring()
    {
        int fired = 0;
        var p = new Pressable {
            Child = new SizedBox(width: 100, height: 40),
            OnPressed = () => fired++,
        };
        p.Measure(Constraints.Tight(width: 100, height: 40));
        p.Layout(Offset.Zero);

        // Touch down arms the press; a scroll gesture then claims the pointer (cancel).
        p.OnPointerDown(new Offset(x: 50, y: 20));
        Assert.True(p.Pressed);
        p.OnPointerCancel();
        Assert.False(p.Pressed);

        // Even if an up still arrives inside bounds afterwards, the tap must not fire.
        p.OnPointerUp(new Offset(x: 50, y: 20));
        Assert.Equal(expected: 0, actual: fired);
    }

    [Fact]
    public void GestureDetector_LongPress_FiresCallbackAndSuppressesTap()
    {
        int taps = 0;
        int longPresses = 0;
        var gd = new GestureDetector(
            child: new SizedBox(width: 100, height: 40),
            onTap: () => taps++,
            onLongPress: () => longPresses++
        );
        gd.Measure(Constraints.Tight(width: 100, height: 40));
        gd.Layout(Offset.Zero);

        var point = new Offset(x: 50, y: 20);
        gd.OnPointerDown(point);
        gd.OnLongPress(point); // the App fires this after the hold threshold
        Assert.Equal(expected: 1, actual: longPresses);

        // The long-press consumed the gesture — the eventual lift is not also a tap.
        gd.OnPointerUp(point);
        Assert.Equal(expected: 0, actual: taps);
    }

    [Fact]
    public void GestureDetector_PointerCancel_SuppressesTap()
    {
        int taps = 0;
        var gd = new GestureDetector(
            child: new SizedBox(width: 100, height: 40),
            onTap: () => taps++
        );
        gd.Measure(Constraints.Tight(width: 100, height: 40));
        gd.Layout(Offset.Zero);

        gd.OnPointerDown(new Offset(x: 50, y: 20));
        gd.OnPointerCancel();
        gd.OnPointerUp(new Offset(x: 50, y: 20));
        Assert.Equal(expected: 0, actual: taps);
    }

    [Fact]
    public void GestureDetector_WithoutLongPressCallback_TapStillFiresAfterHold()
    {
        // Press-hold-release on a plain tappable: the default long-press mapping
        // (context menu via OnRightClick) is a no-op here, so the lift still counts as a tap.
        int taps = 0;
        var gd = new GestureDetector(
            child: new SizedBox(width: 100, height: 40),
            onTap: () => taps++
        );
        gd.Measure(Constraints.Tight(width: 100, height: 40));
        gd.Layout(Offset.Zero);

        var point = new Offset(x: 50, y: 20);
        gd.OnPointerDown(point);
        gd.OnLongPress(point);
        gd.OnPointerUp(point);
        Assert.Equal(expected: 1, actual: taps);
    }

    [Fact]
    public void EventPool_TouchMove_ReusesInstancesAcrossPolls()
    {
        var pool = new EventPool();
        var a = pool.RentTouchMove(
            x: 1f,
            y: 2f,
            finger: 0,
            pressure: 1f,
            windowId: 0
        );
        var b = pool.RentTouchMove(
            x: 3f,
            y: 4f,
            finger: 0,
            pressure: 1f,
            windowId: 0
        );
        Assert.NotSame(
            expected: a,
            actual: b
        ); // two moves within one poll keep distinct coordinates

        pool.Reset();
        var c = pool.RentTouchMove(
            x: 5f,
            y: 6f,
            finger: 1,
            pressure: 0.5f,
            windowId: 7
        );
        Assert.Same(expected: a, actual: c); // next poll reuses the first instance…
        Assert.Equal(expected: 5f, actual: c.X); // …fully overwritten
        Assert.Equal(expected: 6f, actual: c.Y);
        Assert.Equal(expected: 1, actual: c.Finger);
        Assert.Equal(expected: 0.5f, actual: c.Pressure);
        Assert.Equal(expected: 7u, actual: c.WindowId);
    }
}
