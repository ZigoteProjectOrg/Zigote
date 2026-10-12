using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.BottomSheets;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     The Adwaita widgets moved onto springs, checked where a mis-tuned spring would actually hurt:
///     a spec that never settles leaves a dialog that never finishes closing and a frame loop that
///     never goes back to sleep, and a status page that replays its entrance on every property
///     change reads as a flicker.
/// </summary>
[Collection("Ticker")] // these mount widgets that register tickers in the process-wide list
public class AdwMotionTests
{
    private static readonly Constraints Room = new(
        minWidth: 0f,
        maxWidth: 200f,
        minHeight: 0f,
        maxHeight: 200f
    );

    private static PaintList Paint(Widget w, Constraints c)
    {
        w.Measure(c);
        w.Layout(Offset.Zero);
        var list = new PaintList();
        w.Paint(list);
        return list;
    }

    [Fact]
    public void Every_AdwMotion_Spec_Settles_In_Both_Directions()
    {
        // A dialog closes on OnDismissed and an overlay split view hides on it; a spec that only
        // asymptotes would leave both open forever, with the ticker still running.
        (string Name, MotionSpec Spec)[] specs = [
            ("Control", AdwMotion.Control),
            ("Overlay", AdwMotion.Overlay),
            ("Reveal", AdwMotion.Reveal),
            ("Value", AdwMotion.Value),
        ];

        foreach ((string name, var spec) in specs)
        {
            var c = new AnimationController(0.25f) { Spec = spec };
            bool completed = false, dismissed = false;
            c.OnCompleted += () => completed = true;
            c.OnDismissed += () => dismissed = true;

            c.Forward();
            for (int i = 0; i < 240 && !completed; i++) c.Tick(1f / 60f); // 4 s of frames
            Assert.True(condition: completed, userMessage: $"{name} never completed");
            Assert.Equal(expected: 1f, actual: c.Progress, precision: 3);

            c.Reverse();
            for (int i = 0; i < 240 && !dismissed; i++) c.Tick(1f / 60f);
            Assert.True(condition: dismissed, userMessage: $"{name} never dismissed");
            Assert.Equal(expected: 0f, actual: c.Progress, precision: 3);
        }
    }

    /// <summary>
    ///     A switch row's click sets <c>Value</c> from outside; on screen that has to slide the
    ///     knob like a direct tap does, and only an unmounted switch may jump.
    /// </summary>
    [Fact]
    public void Switch_Value_Set_Slides_When_Mounted_And_Snaps_When_Not()
    {
        var off = new AdwSwitch();
        int before = off.DebugStateHash();
        off.Value = true;
        var snapped = new AdwSwitch(true);
        Assert.Equal(expected: snapped.DebugStateHash(), actual: off.DebugStateHash());
        Assert.NotEqual(expected: before, actual: off.DebugStateHash());

        var sw = new AdwSwitch();
        sw.Attach(owner: null!, parent: null);
        sw.Value = true;
        Ticker.AdvanceAll(1f / 60f);
        Assert.NotEqual(expected: snapped.DebugStateHash(), actual: sw.DebugStateHash());
        for (int i = 0; i < 240; i++) Ticker.AdvanceAll(1f / 60f);
        Assert.Equal(expected: snapped.DebugStateHash(), actual: sw.DebugStateHash());
        sw.Detach();
    }

    [Fact]
    public void ProgressBar_Eases_To_A_New_Value_And_Then_Stands_Still()
    {
        var bar = new AdwProgressBar(0f);
        bar.Attach(owner: null!, parent: null);
        var wide = new Constraints(
            minWidth: 0f,
            maxWidth: 100f,
            minHeight: 0f,
            maxHeight: 20f
        );
        Paint(bar, wide);

        bar.Value = 0.8f;

        // One frame in, the fill has started but is nowhere near the target — the point of the
        // change: a bar that jumped would already be at 80.
        Ticker.AdvanceAll(1f / 60f);
        float early = FillWidth(Paint(bar, wide));
        Assert.InRange(actual: early, low: 0.01f, high: 40f);

        for (int i = 0; i < 240; i++) Ticker.AdvanceAll(1f / 60f);
        float settled = FillWidth(Paint(bar, wide));
        Assert.Equal(expected: 80f, actual: settled, precision: 1);

        // And it stops: more frames must not move it (a motion that never settles keeps the ticker
        // running, which keeps the whole event loop awake).
        for (int i = 0; i < 10; i++) Ticker.AdvanceAll(1f / 60f);
        Assert.Equal(expected: settled, actual: FillWidth(Paint(bar, wide)), precision: 4);

        bar.Detach();
    }

    [Fact]
    public void ProgressBar_Set_Before_Mount_Does_Not_Animate_From_Zero()
    {
        // The constructor's value is where the bar starts, not something to animate up to.
        var bar = new AdwProgressBar(0.5f);
        bar.Attach(owner: null!, parent: null);
        float w = FillWidth(
            Paint(
                bar,
                new Constraints(
                    minWidth: 0f,
                    maxWidth: 100f,
                    minHeight: 0f,
                    maxHeight: 20f
                )
            )
        );
        Assert.Equal(expected: 50f, actual: w, precision: 1);
        bar.Detach();
    }

    [Fact]
    public void StatusPage_Keeps_One_Entrance_Across_Rebuilds()
    {
        var page = new AdwStatusPage { Title = "Nothing here", Description = "Yet" };
        page.Attach(owner: null!, parent: null);
        page.Measure(Room);

        var entrance = FindAnimate(page);
        Assert.NotNull(entrance);
        entrance!.Controller.Complete(); // stand in for "the entrance has finished playing"

        // A property change rebuilds the content. A fresh Animate here would be a fresh controller
        // at progress 0 — the page would fade in again on every title change.
        page.Title = "Still nothing";
        page.Measure(Room);

        var after = FindAnimate(page);
        Assert.Same(expected: entrance, actual: after);
        Assert.Equal(expected: 1f, actual: after!.Controller.Progress, precision: 5);

        page.Detach();
    }

    [Fact]
    public void StatusPage_Can_Turn_The_Entrance_Off()
    {
        var page = new AdwStatusPage { Title = "Nothing here", Animated = false };
        page.Attach(owner: null!, parent: null);
        page.Measure(Room);
        Assert.Null(FindAnimate(page));
        page.Detach();
    }

    [Fact]
    public void Sheet_Snap_Carries_The_Release_Velocity_And_Still_Arrives()
    {
        // The flick's speed has to flow into the settle — after the same few frames a flicked sheet
        // is further along than one released from a standstill — and both have to actually arrive,
        // or the sheet is stranded mid-screen with its ticker still running.
        (float After3Frames, int FramesToArrive) Run(float velocity)
        {
            // AnimateTo works in extent fractions, so no available height is needed here — only
            // EndDrag's pixels-per-second conversion cares about it.
            var sheet = new BottomSheetController(
                minExtent: 0.25f,
                initExtent: 0.5f,
                maxExtent: 1f
            );
            sheet.AnimateTo(extent: 1f, velocity: velocity);

            float after3 = 0f;
            int frames = 0;
            while (frames < 600 && MathF.Abs(sheet.Value - 1f) > 0.001f)
            {
                Ticker.AdvanceAll(1f / 60f);
                frames++;
                if (frames == 3) after3 = sheet.Value;
            }

            sheet.Dispose();
            return (after3, frames);
        }

        var lazy = Run(0f);
        var flicked = Run(3f); // three sheet-heights per second, upwards

        Assert.True(condition: lazy.FramesToArrive < 600, userMessage: "a plain snap never arrived");
        Assert.True(
            condition: flicked.FramesToArrive < 600,
            userMessage: "a flicked snap never arrived"
        );
        Assert.True(
            condition: flicked.After3Frames > lazy.After3Frames + 0.01f,
            userMessage:
            $"the release velocity was dropped: {flicked.After3Frames:0.000} vs {lazy.After3Frames:0.000}"
        );
    }

    [Fact]
    public void ToggleGroup_Selection_Slides_Between_Segments()
    {
        var wide = new Constraints(
            minWidth: 0f,
            maxWidth: 500f,
            minHeight: 0f,
            maxHeight: 60f
        );

        var group = new AdwToggleGroup(labels: ["One", "Two", "Three"]);
        group.Measure(wide); // builds the subtree…
        group.Attach(owner: null!, parent: null); // …which this then mounts, tickers and all
        float first = PillX(Paint(group, wide));

        // Where the pill ends up is what a group built on the third segment shows outright.
        var settledGroup = new AdwToggleGroup(labels: ["One", "Two", "Three"], active: 2);
        settledGroup.Measure(wide);
        settledGroup.Attach(owner: null!, parent: null);
        float third = PillX(Paint(settledGroup, wide));
        Assert.True(condition: third > first, userMessage: "the third segment is not to the right");

        group.Active = 2;
        Ticker.AdvanceAll(1f / 60f);
        Ticker.AdvanceAll(1f / 60f);
        float moving = PillX(Paint(group, wide));

        // Mid-slide: past the first segment, not yet at the third. A selection that jumped would
        // already be at `third` on the first frame.
        Assert.True(condition: moving > first + 0.5f, userMessage: "the selection never moved");
        Assert.True(condition: moving < third - 0.5f, userMessage: "the selection jumped");

        for (int i = 0; i < 240; i++) Ticker.AdvanceAll(1f / 60f);
        Assert.Equal(expected: third, actual: PillX(Paint(group, wide)), precision: 1);

        group.Detach();
        settledGroup.Detach();
    }

    [Fact]
    public void ToggleGroup_With_No_Selection_Paints_No_Marker()
    {
        var wide = new Constraints(
            minWidth: 0f,
            maxWidth: 500f,
            minHeight: 0f,
            maxHeight: 60f
        );

        var group = new AdwToggleGroup(labels: ["One", "Two"], active: -1);
        group.Measure(wide);
        group.Attach(owner: null!, parent: null);

        var palette = AdwPalette.For(ThemeProvider.Of(BuildContext.Current));
        foreach (var c in Paint(group, wide).DebugCommands)
        {
            var color = new Color(
                r: c.ColorR,
                g: c.ColorG,
                b: c.ColorB,
                a: c.ColorA
            );
            Assert.NotEqual(expected: palette.ActiveToggleBg, actual: color);
        }

        group.Detach();
    }

    /// <summary>
    ///     The left edge of the selection pill: the one rect painted in the active-toggle colour.
    /// </summary>
    private static float PillX(PaintList list)
    {
        var palette = AdwPalette.For(ThemeProvider.Of(BuildContext.Current));
        float? x = null;
        foreach (var c in list.DebugCommands)
        {
            if (c.Kind != Zigote.Core.Native.ZgPaintOp.Rect) continue;
            var color = new Color(
                r: c.ColorR,
                g: c.ColorG,
                b: c.ColorB,
                a: c.ColorA
            );
            if (color != palette.ActiveToggleBg) continue;
            Assert.Null(x); // exactly one pill, or this assertion is measuring the wrong rect
            x = c.RectX;
        }

        Assert.NotNull(x);
        return x!.Value;
    }

    private static Animate? FindAnimate(Widget root)
    {
        if (root is Animate animate) return animate;
        foreach (var child in root.GetChildren())
        {
            if (FindAnimate(child) is { } found) return found;
        }

        return null;
    }

    private static float FillWidth(PaintList list)
    {
        // The trough spans the whole width; the accent fill is the narrower of the two rects.
        float width = 0f;
        foreach (var c in list.DebugCommands)
        {
            if (c.Kind != Zigote.Core.Native.ZgPaintOp.Rect) continue;
            width = width == 0f ? c.RectW : MathF.Min(x: width, y: c.RectW);
        }

        return width;
    }

}
