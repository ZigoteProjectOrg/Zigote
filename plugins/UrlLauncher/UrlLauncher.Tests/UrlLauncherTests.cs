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
}
