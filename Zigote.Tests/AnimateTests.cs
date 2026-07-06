using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
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

    private static Widget Box()
    {
        return new DecoratedBox { Fill = new Color(1f, 0f, 0f) };
    }

    [Fact]
    public void Headline_Example_Fade500_Scale_Delay500_Totals_1s()
    {
        // Text("Hello").Animate().Fade(500.ms).Scale(delay: 500.ms): scale inherits the 500 ms
        // duration and starts at 500 ms, so the whole timeline is 1 s.
        var a = Box().Animate().Fade(500.ms).Scale(delay: 500.ms);
        a.Measure(Constraints.Tight(20, 20)); // resolves the timeline
        Assert.Equal(1.0f, a.Controller.Duration, 3);
    }

    [Fact]
    public void Effects_Run_In_Parallel_By_Default()
    {
        // No delays → both effects start at 0; scale inherits the fade's 300 ms → total 300 ms.
        var a = Box().Animate().Fade(300.ms).Scale();
        a.Measure(Constraints.Tight(20, 20));
        Assert.Equal(0.3f, a.Controller.Duration, 3);
    }

    [Fact]
    public void Then_Shifts_The_Baseline()
    {
        // fade [0,400], then +100 baseline, move inherits 400 ms → [500,900] → total 900 ms.
        var a = Box().Animate().Fade(400.ms).Then(100.ms).Move();
        a.Measure(Constraints.Tight(20, 20));
        Assert.Equal(0.9f, a.Controller.Duration, 3);
    }

    [Fact]
    public void FadeIn_At_Start_Paints_Nothing()
    {
        // Idle at progress 0 (not attached, so autoplay hasn't run): a fade-in is fully transparent,
        // so Paint short-circuits and emits no commands.
        var a = Box().Animate().FadeIn();
        var p = Paint(a, Constraints.Tight(20, 20));
        Assert.Empty(p.DebugCommands);
    }

    [Fact]
    public void FadeIn_When_Complete_Paints_The_Child()
    {
        var a = Box().Animate().FadeIn();
        a.Controller.Complete(); // jump to the settled end state
        var p = Paint(a, Constraints.Tight(20, 20));
        Assert.Contains(p.DebugCommands, c => (PaintCommandKind)c.Kind == PaintCommandKind.Rect);
    }

    [Fact]
    public void Tick_Is_DeltaTime_Driven_At_Any_FrameRate()
    {
        // A 1 s animation advances by dt/duration each tick regardless of how the frames are sliced:
        // 60 fps (16.67 ms × 60) and 30 fps (33.3 ms × 30) both reach the same place after 1 s.
        var fast = new AnimationController(1f);
        fast.Forward();
        for (var i = 0; i < 60; i++) fast.Tick(1f / 60f);

        var slow = new AnimationController(1f);
        slow.Forward();
        for (var i = 0; i < 30; i++) slow.Tick(1f / 30f);

        Assert.Equal(fast.Progress, slow.Progress, 3);
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
        Assert.Equal(AnimationController.MaxFrameDelta, c.Progress, 3);
        Assert.Equal(AnimationStatus.Forward, c.Status); // still animating, not jumped to Completed
    }

    [Fact]
    public void No_Effects_Is_A_Transparent_Passthrough()
    {
        var a = Box().Animate();
        var p = Paint(a, Constraints.Tight(20, 20));
        Assert.Contains(p.DebugCommands, c => (PaintCommandKind)c.Kind == PaintCommandKind.Rect);
    }
}