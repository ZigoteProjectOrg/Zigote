using Xunit;

namespace AppSettings.Tests;

public class AppSettingsTests
{
    [Fact]
    public async Task Desktop_HasNoAppSettingsPage()
    {
        foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
            Assert.False(await AppSettingsPlugin.OpenAsync(page));
    }
}
