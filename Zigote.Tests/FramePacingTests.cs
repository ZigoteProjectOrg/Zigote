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

    // ── background budget: slack minus what the rest of the frame still needs ──

    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    [Fact]
    public void BackgroundBudget_ReservesTheRestOfTheFrame()
    {
        // 120 Hz, 1 ms spent, 5 ms of layout+paint expected: 8.33 - 1 - 5 ≈ 2.33 ms left.
        double budget = App.BackgroundBudgetMs(
            intervalTicks: TicksFor(120),
            spentTicks: Ms(1),
            reserveTicks: Ms(5)
        );
        Assert.InRange(actual: budget, low: 2.2, high: 2.4);
    }

    [Fact]
    public void BackgroundBudget_IsCapped_AndNeverNegative()
    {
        Assert.Equal(
            expected: 4d,
            actual: App.BackgroundBudgetMs(
                intervalTicks: TicksFor(30),
                spentTicks: 0,
                reserveTicks: 0
            )
        );
        Assert.Equal(
            expected: 0d,
            actual: App.BackgroundBudgetMs(
                intervalTicks: TicksFor(120),
                spentTicks: Ms(2),
                reserveTicks: Ms(20)
            )
        );
    }

    [Fact]
    public void FrameReserve_JumpsUp_AndDecaysSlowly()
    {
        long reserve = App.NextFrameReserve(currentTicks: Ms(1), sampleTicks: Ms(6));
        Assert.Equal(expected: Ms(6), actual: reserve);

        // One cheap frame barely moves it; a second of them brings it most of the way back.
        reserve = App.NextFrameReserve(currentTicks: reserve, sampleTicks: Ms(1));
        Assert.InRange(actual: reserve, low: Ms(5.7), high: Ms(5.8));
        for (int i = 0; i < 120; i++)
            reserve = App.NextFrameReserve(currentTicks: reserve, sampleTicks: Ms(1));
        Assert.InRange(actual: reserve, low: Ms(1), high: Ms(1.1));
    }

    // ── caret blink: the frame loop repaints on its edges and sleeps between them ──

    [Theory]
    [InlineData(0f, true, 0.6f)] // just restarted: solid, hides in 0.6 s
    [InlineData(0.5f, true, 0.1f)]
    [InlineData(0.7f, false, 0.36f)] // hidden, reappears at the end of the cycle
    [InlineData(1.06f + 0.2f, true, 0.4f)] // second cycle
    public void CaretBlink_ReportsStateAndTimeToNextEdge(float sinceRestart, bool shown,
        float changeIn)
    {
        const float blinkBase = 12.5f;
        float time = blinkBase + sinceRestart;

        Assert.Equal(
            expected: shown,
            actual: UI.Widgets.CaretBlink.Shown(time: time, blinkBase: blinkBase)
        );
        Assert.Equal(
            expected: changeIn,
            actual: UI.Widgets.CaretBlink.ChangeIn(time: time, blinkBase: blinkBase),
            tolerance: 0.001f
        );
    }

    [Fact]
    public void CaretBlink_EdgeTimesAgreeWithShown()
    {
        // Stepping exactly to each reported edge must flip the state every time.
        float time = 3f;
        bool shown = UI.Widgets.CaretBlink.Shown(time: time, blinkBase: 0f);
        for (int i = 0; i < 8; i++)
        {
            time += UI.Widgets.CaretBlink.ChangeIn(time: time, blinkBase: 0f) + 0.001f;
            bool next = UI.Widgets.CaretBlink.Shown(time: time, blinkBase: 0f);
            Assert.NotEqual(expected: shown, actual: next);
            shown = next;
        }
    }
}
