using Xunit;
using Zigote.Core.Animation;

namespace Zigote.Tests;

/// <summary>
///     The checks that fail if <see cref="Motion" /> stops being what the animation system is built
///     on: a spring that settles, stops, ignores the frame rate, and keeps its velocity through a
///     retarget — plus the decay a fling rides.
/// </summary>
public class MotionTests
{
    private static float RunToRest(ref Motion m, float dt, int maxTicks = 4000)
    {
        float seconds = 0f;
        for (int i = 0; i < maxTicks; i++)
        {
            if (!m.Tick(dt)) return seconds;
            seconds += dt;
        }

        return float.NaN; // never settled — the caller asserts on this
    }

    [Fact]
    public void Spring_Settles_And_Reports_Done()
    {
        var m = new Motion(spec: MotionSpec.Smooth, value: 0f, target: 1f);
        float seconds = RunToRest(ref m, dt: 1f / 60f);

        Assert.False(float.IsNaN(seconds)); // a spring that never stops keeps the frame loop awake
        Assert.Equal(expected: 1f, actual: m.Value, precision: 3);
        Assert.Equal(expected: 0f, actual: m.Velocity, precision: 3);
        Assert.True(m.IsSettled);
        Assert.InRange(actual: seconds, low: 0.1f, high: 2f);
    }

    [Fact]
    public void Spring_Settle_Time_Is_Frame_Rate_Independent()
    {
        // The whole reason for the fixed internal substep: the same spec must behave the same on a
        // 30 Hz phone and a 144 Hz desktop, and deterministically on the fixed gameplay clock.
        float[] rates = [30f, 60f, 144f];
        float first = float.NaN;
        foreach (float hz in rates)
        {
            var m = new Motion(spec: MotionSpec.Smooth, value: 0f, target: 1f);
            float seconds = RunToRest(ref m, dt: 1f / hz);
            Assert.False(float.IsNaN(seconds));
            if (float.IsNaN(first)) first = seconds;
            else Assert.InRange(actual: seconds, low: first * 0.85f, high: first * 1.15f);
        }
    }

    [Fact]
    public void Retarget_Keeps_Velocity()
    {
        var m = new Motion(spec: MotionSpec.Smooth, value: 0f, target: 1f);
        for (int i = 0; i < 6; i++) m.Tick(1f / 60f);

        float flying = m.Velocity;
        Assert.True(flying > 0f);

        m.Retarget(0.5f);
        Assert.Equal(expected: flying, actual: m.Velocity, precision: 5);
        Assert.Equal(expected: 0.5f, actual: m.Target, precision: 5);
    }

    [Fact]
    public void Duration_Retarget_Restarts_From_Current_Value()
    {
        var m = new Motion(
            spec: MotionSpec.Duration(seconds: 0.2f),
            value: 0f,
            target: 1f
        );
        for (int i = 0; i < 6; i++) m.Tick(1f / 60f);
        float midway = m.Value;
        Assert.InRange(actual: midway, low: 0.1f, high: 0.9f);

        // A duration curve cannot honour an incoming velocity; it must at least stay continuous in
        // position, which is what a restart from the current value gives.
        m.Retarget(0f);
        Assert.Equal(expected: midway, actual: m.Value, precision: 5);
        Assert.False(float.IsNaN(RunToRest(ref m, dt: 1f / 60f)));
        Assert.Equal(expected: 0f, actual: m.Value, precision: 4);
    }

    [Fact]
    public void Bouncy_Spring_Overshoots_Critical_Does_Not()
    {
        float Peak(MotionSpec spec)
        {
            var m = new Motion(spec: spec, value: 0f, target: 1f);
            float peak = 0f;
            for (int i = 0; i < 600 && m.Tick(1f / 120f); i++) peak = MathF.Max(x: peak, y: m.Value);
            return peak;
        }

        Assert.True(Peak(MotionSpec.Bouncy) > 1.02f);
        Assert.True(Peak(MotionSpec.Smooth) <= 1.001f);
    }

    [Fact]
    public void Decay_Stops_Near_Its_Projection()
    {
        var m = new Motion(
            spec: MotionSpec.Decay(friction: 3.2f) with { Epsilon = 0.5f },
            value: 0f,
            velocity: 1200f
        );
        float projected = m.Projected;
        Assert.Equal(expected: 375f, actual: projected, precision: 0); // 1200 / 3.2

        Assert.False(float.IsNaN(RunToRest(ref m, dt: 1f / 60f)));
        // It stops once it is crawling (below Epsilon per 60 Hz frame), which leaves at most one
        // more velocity-decay's worth of travel unrun — the same trade the old scroller's
        // MinFlingSpeed made, and the reason Projected is a prediction rather than a promise.
        float slack = MotionSpec.Decay(friction: 3.2f).Friction is var f and > 0f
            ? 0.5f * 60f / f
            : 0f;
        Assert.InRange(actual: m.Value, low: projected - slack - 1f, high: projected + 1f);
        Assert.Equal(expected: 0f, actual: m.Velocity, precision: 4);
    }

    [Fact]
    public void Controller_With_Spring_Spec_Completes_And_Stops()
    {
        var c = new AnimationController(0.25f) { Spec = MotionSpec.Snappy };
        bool completed = false;
        c.OnCompleted += () => completed = true;
        c.Forward();

        for (int i = 0; i < 600 && c.Status == AnimationStatus.Forward; i++) c.Tick(1f / 60f);

        Assert.True(completed);
        Assert.Equal(expected: AnimationStatus.Completed, actual: c.Status);
        Assert.Equal(expected: 1f, actual: c.Value, precision: 3);
    }

    [Fact]
    public void Controller_Reverse_Midflight_Keeps_Velocity()
    {
        var c = new AnimationController(0.25f) { Spec = MotionSpec.Smooth };
        c.Forward();
        for (int i = 0; i < 4; i++) c.Tick(1f / 60f);
        float flying = c.Velocity;
        Assert.True(flying > 0f);

        c.Reverse();
        Assert.Equal(expected: flying, actual: c.Velocity, precision: 5);
    }

    [Fact]
    public void ReduceMotion_Jumps_To_The_End_And_Still_Fires()
    {
        AnimationController.ReduceMotion = true;
        try
        {
            var c = new AnimationController(0.4f);
            bool completed = false;
            c.OnCompleted += () => completed = true;
            c.Forward();

            Assert.True(completed);
            Assert.Equal(expected: AnimationStatus.Completed, actual: c.Status);
            Assert.Equal(expected: 1f, actual: c.Progress, precision: 5);

            // A spinner is not decoration: freezing it under reduced motion reads as a hang.
            var spinner = new AnimationController(1f);
            spinner.Repeat();
            spinner.Tick(0.25f);
            Assert.Equal(expected: AnimationStatus.Forward, actual: spinner.Status);
        }
        finally
        {
            AnimationController.ReduceMotion = false;
        }
    }

    [Fact]
    public void TimeScale_Slows_Everything_Down()
    {
        AnimationController.TimeScale = 0.5f;
        try
        {
            var c = new AnimationController(1f) { Curve = Curves.Linear };
            c.Forward();
            c.Tick(0.1f); // MaxFrameDelta caps the raw dt; TimeScale halves what is left
            Assert.Equal(expected: 0.05f, actual: c.Progress, precision: 4);

            AnimationController.TimeScale = 1f;
            c.Dismiss();
            c.Forward();
            c.Tick(0.1f);
            Assert.Equal(expected: 0.1f, actual: c.Progress, precision: 4);
        }
        finally
        {
            AnimationController.TimeScale = 1f;
        }
    }

    [Fact]
    public void Bezier_Matches_Its_Control_Points_At_The_Ends_And_Is_Monotonic()
    {
        var ease = Curves.Bezier(
            x1: 0.25f,
            y1: 0.1f,
            x2: 0.25f,
            y2: 1f
        );
        Assert.Equal(expected: 0f, actual: ease(0f), precision: 5);
        Assert.Equal(expected: 1f, actual: ease(1f), precision: 5);

        float previous = 0f;
        for (int i = 1; i <= 100; i++)
        {
            float v = ease(i / 100f);
            Assert.True(v >= previous - 1e-4f);
            previous = v;
        }
    }

    [Fact]
    public void Interval_Holds_Before_And_After_Its_Window()
    {
        var staggered = Curves.Interval(begin: 0.5f, end: 1f, curve: Curves.Linear);
        Assert.Equal(expected: 0f, actual: staggered(0.25f), precision: 5);
        Assert.Equal(expected: 0f, actual: staggered(0.5f), precision: 5);
        Assert.Equal(expected: 0.5f, actual: staggered(0.75f), precision: 5);
        Assert.Equal(expected: 1f, actual: staggered(1f), precision: 5);
    }

    [Fact]
    public void Every_Easing_Starts_At_Zero_And_Ends_At_One()
    {
        Func<float, float>[] all = [
            Curves.Linear, Curves.EaseIn, Curves.EaseOut, Curves.EaseInOut,
            Curves.EaseInCubic, Curves.EaseOutCubic, Curves.EaseInOutCubic,
            Curves.EaseInQuart, Curves.EaseOutQuart, Curves.EaseInOutQuart,
            Curves.EaseInQuint, Curves.EaseOutQuint, Curves.EaseInOutQuint,
            Curves.EaseInSine, Curves.EaseOutSine, Curves.EaseInOutSine,
            Curves.EaseInExpo, Curves.EaseOutExpo, Curves.EaseInOutExpo,
            Curves.EaseInCirc, Curves.EaseOutCirc, Curves.EaseInOutCirc,
            Curves.EaseInBack, Curves.EaseOutBack, Curves.EaseInOutBack,
            Curves.EaseInElastic, Curves.ElasticOut, Curves.EaseInOutElastic,
            Curves.EaseInBounce, Curves.BounceOut, Curves.EaseInOutBounce,
            Curves.Ease, Curves.Spring,
        ];

        foreach (var curve in all)
        {
            Assert.Equal(expected: 0f, actual: curve(0f), precision: 3);
            Assert.Equal(expected: 1f, actual: curve(1f), precision: 3);
        }
    }
}
