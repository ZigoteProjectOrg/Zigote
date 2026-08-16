using System.Diagnostics;
using Xunit;
using Zigote.UI.Host;

namespace Zigote.Tests;

/// <summary>
///     Guards the one place the app's target frame rate is decided (<c>App.FrameIntervalTicks</c>).
///     The invariant: the monitor's refresh is the ceiling and an explicit FPS cap can only ever slow
///     the loop below it. Getting the comparison backwards would either pin a 144 Hz panel to 60 or
///     let a game's "240 fps" request spin frames the display can never show.
/// </summary>
public class FramePacingTests
{
    private static long TicksFor(double fps) => (long)(Stopwatch.Frequency / fps);

    [Theory]
    [InlineData(60f)]
    [InlineData(144f)]
    [InlineData(239.76f)] // a real panel's non-integer reported rate
    public void NoCap_FollowsTheDisplay(float hz) => Assert.Equal(
        expected: TicksFor(hz),
        actual: App.ComputeFrameIntervalTicks(displayHz: hz, frameRateLimit: 0)
    );

    [Fact]
    public void CapBelowRefresh_Wins()
    {
        // 30 fps requested on a 144 Hz panel → 30 fps.
        Assert.Equal(
            expected: TicksFor(30),
            actual: App.ComputeFrameIntervalTicks(displayHz: 144f, frameRateLimit: 30)
        );
    }

    [Fact]
    public void CapAboveRefresh_ClampsToTheDisplay()
    {
        // 240 fps requested on a 144 Hz panel → 144 fps, not 240.
        Assert.Equal(
            expected: TicksFor(144f),
            actual: App.ComputeFrameIntervalTicks(displayHz: 144f, frameRateLimit: 240)
        );
    }

    [Fact]
    public void UnknownRefreshRate_FallsBackTo60()
    {
        // SDL reports 0 on some drivers / headless; the loop must not divide by zero or spin.
        Assert.Equal(
            expected: TicksFor(60),
            actual: App.ComputeFrameIntervalTicks(displayHz: 0f, frameRateLimit: 0)
        );
    }

    [Fact]
    public void MovingBetweenMonitors_ChangesThePace()
    {
        // The 60 Hz → 144 Hz drag the multi-monitor support exists for.
        Assert.True(
            App.ComputeFrameIntervalTicks(displayHz: 144f, frameRateLimit: 0) <
            App.ComputeFrameIntervalTicks(displayHz: 60f, frameRateLimit: 0)
        );
    }

    /// <summary>
    ///     Guards the animation-dt snap (<c>App.ComputeAnimationDt</c>): present jitter near a
    ///     whole number of refresh intervals is flattened so integrators (scroll ease, flings)
    ///     don't turn time noise into position noise, while genuine hitches pass through raw.
    /// </summary>
    [Theory]
    [InlineData(0.0161f, 1f / 60f)] // jittered-short frame → snapped to one interval
    [InlineData(0.0172f, 1f / 60f)] // jittered-long frame → snapped
    [InlineData(0.0334f, 2f / 60f)] // missed one vsync → snapped to exactly two
    [InlineData(0.0250f, 0.0250f)] // halfway between multiples → raw (a real irregularity)
    [InlineData(0.1000f, 0.1000f)] // hitch past 3 intervals → raw
    [InlineData(0.0020f, 0.0020f)] // sub-interval dt (unpaced loop) → raw
    public void AnimationDt_SnapsJitterOnly(float dt, float expected) => Assert.Equal(
        expected: expected,
        actual: App.ComputeAnimationDt(dt: dt, interval: 1f / 60f),
        precision: 5
    );

    [Fact]
    public void AnimationDt_NoInterval_PassesThrough() =>
        Assert.Equal(expected: 0.016f, actual: App.ComputeAnimationDt(dt: 0.016f, interval: 0f));
}
