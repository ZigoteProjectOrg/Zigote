using PaintCommandKind = Zigote.Core.Native.ZgPaintOp;
using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Headless tests for the zigote_animate fluent API (<see cref="Animate" />). Cover the
///     flutter_animate-faithful timeline (parallel-by-default, delay/duration inheritance,
///     <c>.Then()</c> baseline shift) via the public <see cref="Animate.Controller" />, and the paint
///     behaviour at the timeline's ends.
/// </summary>
public class AnimateTests
{
    private static PaintList Paint(Widget w, Constraints c)
    {
        w.Measure(c);
        w.Layout(Offset.Zero);
        var p = new PaintList();
        w.Paint(p);
        return p;
    }

    private static Widget Box() => new DecoratedBox { Fill = new Color(r: 1f, g: 0f, b: 0f) };

    [Fact]
    public void Rotate_Pushes_A_Transform_And_Unwinds_To_Square()
    {
        // Mid-rotation the child has to go through the native transform stack; at the end of the
        // timeline the rotation is zero again and the cheap translate path takes over.
        var a = Box().Animate().Rotate(begin: -0.25f, end: 0f);
        var mid = Paint(a, Constraints.Tight(width: 20, height: 20));
        Assert.Contains(mid.DebugCommands, c => (PaintCommandKind)c.Kind == PaintCommandKind.TransformPush);

        a.Controller.Complete();
        var end = Paint(a, Constraints.Tight(width: 20, height: 20));
        Assert.DoesNotContain(
            end.DebugCommands,
            c => (PaintCommandKind)c.Kind == PaintCommandKind.TransformPush
        );
    }

    [Fact]
    public void Rotated_HitTest_Follows_The_Painted_Position()
    {
        // A quarter turn puts the child's top-left corner where its bottom-left was; a point that
        // misses the unrotated box has to hit the rotated one and vice versa.
        var a = Box().Animate().Rotate(begin: 0.25f, end: 0f);
        a.Measure(Constraints.Tight(width: 40, height: 10));
        a.Layout(Offset.Zero);

        Assert.NotNull(a.HitTest(new Offset(x: 20f, y: 20f)));
        Assert.Null(a.HitTest(new Offset(x: 38f, y: 5f)));
    }

    [Fact]
    public void Stagger_Delays_Each_Child_By_One_Step()
    {
        List<Widget> rows = [Box(), Box(), Box()];
        var staggered = rows.Stagger(step: 100.ms);

        for (int i = 0; i < staggered.Count; i++)
        {
            var a = Assert.IsType<Animate>(staggered[i]);
            a.Measure(Constraints.Tight(width: 20, height: 20)); // resolves the timeline
            // Each child's timeline ends one step later than the one before it.
            Assert.Equal(
                expected: (i * 0.1f) + (float)Animate.DefaultDuration.TotalSeconds,
                actual: a.Controller.Duration,
                precision: 3
            );
        }
    }

    [Fact]
    public void Loop_Presets_Ask_The_Controller_To_Repeat()
    {
        var pulse = Box().Pulse(duration: 400.ms);
        Assert.True(pulse.Loop);
        Assert.True(pulse.LoopReverse);

        var spin = Box().Spin(duration: 1.seconds);
        Assert.True(spin.Loop);
        Assert.False(spin.LoopReverse); // a spinner wraps, it does not ping-pong
    }

    [Fact]
    public void Presets_Chain_Onto_One_Timeline_Instead_Of_Nesting()
    {
        // .PopIn().Wobble() must build a single Animate — nesting would double the paint work and
        // give the two halves separate controllers.
        var a = Box().PopIn().Wobble(delay: 300.ms);
        Assert.IsType<Animate>(a);
        Assert.IsNotType<Animate>(a.Child);
    }

    [Fact]
    public void Headline_Example_Fade500_Scale_Delay500_Totals_1s()
    {
        // Text("Hello").Animate().Fade(500.ms).Scale(delay: 500.ms): scale inherits the 500 ms
        // duration and starts at 500 ms, so the whole timeline is 1 s.
        var a = Box().Animate().Fade(500.ms).Scale(delay: 500.ms);
        a.Measure(Constraints.Tight(width: 20, height: 20)); // resolves the timeline
        Assert.Equal(expected: 1.0f, actual: a.Controller.Duration, precision: 3);
    }

    [Fact]
    public void Effects_Run_In_Parallel_By_Default()
    {
        // No delays → both effects start at 0; scale inherits the fade's 300 ms → total 300 ms.
        var a = Box().Animate().Fade(300.ms).Scale();
        a.Measure(Constraints.Tight(width: 20, height: 20));
        Assert.Equal(expected: 0.3f, actual: a.Controller.Duration, precision: 3);
    }

    [Fact]
    public void Then_Shifts_The_Baseline()
    {
        // fade [0,400], then +100 baseline, move inherits 400 ms → [500,900] → total 900 ms.
        var a = Box().Animate().Fade(400.ms).Then(100.ms).Move();
        a.Measure(Constraints.Tight(width: 20, height: 20));
        Assert.Equal(expected: 0.9f, actual: a.Controller.Duration, precision: 3);
    }

    [Fact]
    public void FadeIn_At_Start_Paints_Nothing()
    {
        // Idle at progress 0 (not attached, so autoplay hasn't run): a fade-in is fully transparent,
        // so Paint short-circuits and emits no commands.
        var a = Box().Animate().FadeIn();
        var p = Paint(w: a, c: Constraints.Tight(width: 20, height: 20));
        Assert.Empty(p.DebugCommands);
    }

    [Fact]
    public void FadeIn_When_Complete_Paints_The_Child()
    {
        var a = Box().Animate().FadeIn();
        a.Controller.Complete(); // jump to the settled end state
        var p = Paint(w: a, c: Constraints.Tight(width: 20, height: 20));
        Assert.Contains(
            collection: p.DebugCommands,
            filter: c => (PaintCommandKind)c.Kind == PaintCommandKind.Rect
        );
    }

    [Fact]
    public void Tick_Is_DeltaTime_Driven_At_Any_FrameRate()
    {
        // A 1 s animation advances by dt/duration each tick regardless of how the frames are sliced:
        // 60 fps (16.67 ms × 60) and 30 fps (33.3 ms × 30) both reach the same place after 1 s.
        var fast = new AnimationController(1f);
        fast.Forward();
        for (int i = 0; i < 60; i++) fast.Tick(1f / 60f);

        var slow = new AnimationController(1f);
        slow.Forward();
        for (int i = 0; i < 30; i++) slow.Tick(1f / 30f);

        Assert.Equal(expected: fast.Progress, actual: slow.Progress, precision: 3);
        Assert.True(fast.Progress >= 0.999f);
    }

    [Fact]
    public void Tick_Clamps_A_Stalled_Frame_So_It_Cannot_Jump()
    {
        // A huge delta (GC pause / load hitch / debugger break) advances by at most MaxFrameDelta,
        // not the full elapsed time — so the animation doesn't skip or finish in one frame.
        var c = new AnimationController(1f);
        c.Forward();
        c.Tick(10f); // a 10-second stall
        Assert.Equal(expected: AnimationController.MaxFrameDelta, actual: c.Progress, precision: 3);
        Assert.Equal(
            expected: AnimationStatus.Forward,
            actual: c.Status
        ); // still animating, not jumped to Completed
    }

    [Fact]
    public void No_Effects_Is_A_Transparent_Passthrough()
    {
        var a = Box().Animate();
        var p = Paint(w: a, c: Constraints.Tight(width: 20, height: 20));
        Assert.Contains(
            collection: p.DebugCommands,
            filter: c => (PaintCommandKind)c.Kind == PaintCommandKind.Rect
        );
    }
}
