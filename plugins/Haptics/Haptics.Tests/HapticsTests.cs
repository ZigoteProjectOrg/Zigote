using Xunit;

namespace Haptics.Tests;

/// <summary>Desktop has no haptics, so what is left to check is the pattern table and the clamping.</summary>
public class HapticsTests
{
    [Fact]
    public void Patterns_StartWithNoDelay_AndAlternateOnOff()
    {
        foreach (Haptic feedback in Enum.GetValues<Haptic>())
        {
            var (timings, amplitude) = HapticsPlugin.PatternFor(feedback);
            Assert.Equal(0, timings[0]);
            Assert.Equal(0, timings.Length % 2);
            Assert.All(timings[1..], slot => Assert.True(slot > 0));
            Assert.InRange(amplitude, 0.1, 1.0);
        }
    }

    [Fact]
    public void Vibrate_RejectsNothingToFeel()
    {
        Assert.False(HapticsPlugin.Vibrate(TimeSpan.Zero));
        Assert.False(HapticsPlugin.Vibrate(TimeSpan.FromSeconds(1), amplitude: 0));
        Assert.False(HapticsPlugin.Supported); // desktop
    }
}
