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
    private static long TicksFor(double fps)
    {
        return (long)(Stopwatch.Frequency / fps);
    }

    [Theory]
    [InlineData(60f)]
    [InlineData(144f)]
    [InlineData(239.76f)] // a real panel's non-integer reported rate
    public void NoCap_FollowsTheDisplay(float hz)
    {
        Assert.Equal(TicksFor(hz), App.ComputeFrameIntervalTicks(hz, 0));
    }

    [Fact]
    public void CapBelowRefresh_Wins()
    {
        // 30 fps requested on a 144 Hz panel → 30 fps.
        Assert.Equal(TicksFor(30), App.ComputeFrameIntervalTicks(144f, 30));
    }

    [Fact]
    public void CapAboveRefresh_ClampsToTheDisplay()
    {
        // 240 fps requested on a 144 Hz panel → 144 fps, not 240.
        Assert.Equal(TicksFor(144f), App.ComputeFrameIntervalTicks(144f, 240));
    }

    [Fact]
    public void UnknownRefreshRate_FallsBackTo60()
    {
        // SDL reports 0 on some drivers / headless; the loop must not divide by zero or spin.
        Assert.Equal(TicksFor(60), App.ComputeFrameIntervalTicks(0f, 0));
    }

    [Fact]
    public void MovingBetweenMonitors_ChangesThePace()
    {
        // The 60 Hz → 144 Hz drag the multi-monitor support exists for.
        Assert.True(App.ComputeFrameIntervalTicks(144f, 0) < App.ComputeFrameIntervalTicks(60f, 0));
    }
}