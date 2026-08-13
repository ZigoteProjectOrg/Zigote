using Xunit;
using Zigote.Scripting;

namespace Zigote.Tests;

public class TimeInterpolationAlphaTests
{
    // The host publishes the fixed-loop leftover fraction after each frame's tick loop; a replay
    // must start with no stale alpha from the previous session.
    [Fact]
    public void Reset_Clears_The_Interpolation_Alpha()
    {
        Time._interpolationAlpha = 0.75f;

        Time.Reset();

        Assert.Equal(expected: 0f, actual: Time.InterpolationAlpha);
    }

    [Fact]
    public void InterpolationAlpha_Reads_The_Host_Written_Fraction()
    {
        Time._interpolationAlpha = 0.25f;
        Assert.Equal(expected: 0.25f, actual: Time.InterpolationAlpha);
        Time.Reset();
    }
}
