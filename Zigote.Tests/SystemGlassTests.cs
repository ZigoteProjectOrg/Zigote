using Xunit;
using Zigote.Core;
using Zigote.UI.Theme;

namespace Zigote.Tests;

public class SystemGlassTests
{
    [Fact]
    public void System_glass_preferences_move_only_what_they_should()
    {
        var haze = new Color(r: 1f, g: 1f, b: 1f, a: 0.18f);
        try
        {
            SystemGlass.ReduceTransparency = false;
            SystemGlass.Tinted = 0f;
            Assert.Equal(expected: (haze, 0.65f), actual: Glass.ApplySystem(ThemeData.Light, haze, 0.65f));

            // Tinted thickens regular glass, keeps the family, and leaves clear glass alone.
            SystemGlass.Tinted = 1f;
            var (tinted, adapt) = Glass.ApplySystem(ThemeData.Dark, haze, -0.65f);
            Assert.True(tinted.A > 0.4f && tinted.R < 0.2f && adapt == -1f);
            Assert.Equal(expected: (haze, 0.65f), actual: Glass.ApplySystem(ThemeData.Light, haze, 0.65f, weight: 0f));

            // A scrim already past the target (latched / accent glass) is untouched.
            var latched = Color.White.WithAlpha(0.92f);
            Assert.Equal(expected: latched, actual: Glass.ApplySystem(ThemeData.Dark, latched, 0.65f).Tint);

            // Reduce transparency frosts even clear glass.
            SystemGlass.ReduceTransparency = true;
            Assert.True(Glass.ApplySystem(ThemeData.Light, haze, 0f, weight: 0f).Tint.A >= 0.89f);
        }
        finally
        {
            SystemGlass.ReduceTransparency = false;
            SystemGlass.Tinted = 0f;
        }
    }
}
