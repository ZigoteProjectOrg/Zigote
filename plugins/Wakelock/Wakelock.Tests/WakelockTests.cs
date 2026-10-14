using Xunit;

namespace Wakelock.Tests;

/// <summary>
///     Runs against the real OS: on macOS and Windows a hold is a genuine power assertion, taken and
///     released inside the test. A Linux box without a screensaver service checks the honest refusal.
/// </summary>
public class WakelockTests
{
    [Fact]
    public void EnableThenDisable_TracksState_AndIsIdempotent()
    {
        // A bus with no screensaver service (headless CI) refuses too — and must not claim a hold.
        if (!WakelockPlugin.Enable())
        {
            Assert.False(WakelockPlugin.Enabled);
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) Assert.Fail("OS refused the power assertion");
            return;
        }

        try
        {
            Assert.True(WakelockPlugin.Enabled);
            Assert.True(WakelockPlugin.Enable()); // already held — no second assertion
        }
        finally
        {
            Assert.True(WakelockPlugin.Disable());
        }

        Assert.False(WakelockPlugin.Enabled);
        Assert.True(WakelockPlugin.Disable()); // never held is not an error
    }
}
