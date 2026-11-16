using Xunit;

namespace UrlLauncher.Tests;

public class UrlLauncherTests
{
    // Only the blank-input contract: actually opening a URL would launch a browser on
    // whatever machine runs the tests.
    [Fact]
    public async Task TryOpenAsync_BlankIsFalse()
    {
        Assert.False(await UrlLauncherPlugin.TryOpenAsync(""));
        Assert.False(await UrlLauncherPlugin.TryOpenAsync("   "));
        Assert.False(await UrlLauncherPlugin.TryOpenAsync(null!));
    }

    // The launcher runs whatever it is handed, so everything below must be refused before it:
    // none of these may reach the platform.
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("FILE:///usr/bin/true")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("/usr/bin/true")]
    [InlineData("calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("not a url")]
    public async Task TryOpenAsync_RefusesAnythingButAnAllowedAbsoluteUrl(string url)
    {
        Assert.False(UrlLauncherPlugin.IsAllowed(url: url, allowedSchemes: UrlLauncherPlugin.DefaultSchemes));
        Assert.False(await UrlLauncherPlugin.TryOpenAsync(url));
    }

    [Theory]
    [InlineData("https://example.com/a?b=c")]
    [InlineData("HTTP://example.com")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+15550100")]
    public void DefaultSchemes_CoverWebMailAndPhone(string url) =>
        Assert.True(UrlLauncherPlugin.IsAllowed(url: url, allowedSchemes: UrlLauncherPlugin.DefaultSchemes));

    [Fact]
    public void AnotherScheme_IsOpenedOnlyWhenNamed()
    {
        Assert.False(UrlLauncherPlugin.IsAllowed(url: "myapp://callback", allowedSchemes: UrlLauncherPlugin.DefaultSchemes));
        Assert.True(UrlLauncherPlugin.IsAllowed(url: "myapp://callback", allowedSchemes: new HashSet<string> { "MyApp" }));
    }
}
