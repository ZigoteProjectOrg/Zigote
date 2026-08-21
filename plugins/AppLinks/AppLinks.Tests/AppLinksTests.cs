using Xunit;

namespace AppLinks.Tests;

/// <summary>
///     What counts as a link, and the desktop handoff — the part that decides whether a second
///     launch opens a window or feeds the running app.
/// </summary>
public class AppLinksTests
{
    [Theory]
    [InlineData("myapp://auth/callback?code=1", true)]
    [InlineData("https://example.org/invite/42", true)]
    [InlineData("--fullscreen", false)]
    [InlineData("relative/path.txt", false)]
    [InlineData("/usr/share/doc.pdf", false)]  // a document, not a link
    [InlineData("", false)]
    public void OnlyAbsoluteNonFileUrisAreLinks(string candidate, bool isLink)
        => Assert.Equal(isLink, AppLinksPlugin.TryParse(candidate) is not null);

    [Fact]
    public void LinksAreLiftedOutOfACommandLine()
        => Assert.Equal(
            ["myapp://open/1"],
            AppLinksPlugin.Links(["--verbose", "myapp://open/1", "notes.txt"]));

    [Fact]
    public async Task SecondInstanceHandsItsLinksToTheFirst_AndIsToldToExit()
    {
        string appId = "dev.zigote.tests." + Guid.NewGuid().ToString("N");
        List<string> received = [];
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool first = await AppLinksDriver.StartAsync(appId, [], link =>
        {
            received.Add(link);
            delivered.TrySetResult(link);
        });
        Assert.True(first);   // nobody else running: this instance owns the app

        bool second = await AppLinksDriver.StartAsync(
            appId, ["myapp://from/second"], _ => { });
        Assert.False(second); // handed over — the caller is expected to exit

        string link = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("myapp://from/second", link);
        Assert.Equal(["myapp://from/second"], received);
    }

    [Fact]
    public async Task StartRequiresAnAppId()
        => await Assert.ThrowsAsync<ArgumentException>(() => AppLinksPlugin.StartAsync(" "));
}
