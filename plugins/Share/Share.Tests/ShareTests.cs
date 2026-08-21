using Xunit;

namespace Share.Tests;

/// <summary>
///     The parts that decide something: what counts as shareable input, and what the desktop
///     fallback puts on the clipboard. No engine in the test process, so the clipboard write is a
///     no-op and no sheet can open.
/// </summary>
public class ShareTests
{
    [Fact]
    public async Task BlankText_IsUnavailable()
    {
        Assert.Equal(ShareStatus.Unavailable, await SharePlugin.ShareTextAsync("   "));
        Assert.Equal(ShareStatus.Unavailable, await SharePlugin.ShareFilesAsync([]));
        Assert.Equal(ShareStatus.Unavailable, await SharePlugin.ShareFilesAsync(["/no/such/file"]));
    }

    [Fact]
    public void Existing_KeepsRealFilesOnly_AsFullPaths()
    {
        string file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(file, "x");
        try
        {
            Assert.Equal(
                [file],
                SharePlugin.Existing([file, "", "/no/such/file", Path.GetTempPath()]));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DesktopCompose_PutsSubjectThenTextThenPaths()
    {
        Assert.Equal(
            "Trip\nLook at this\n/a.png\n/b.png",
            ShareDriver.Compose("Look at this", "Trip", ["/a.png", "/b.png"]));
        Assert.Equal("hello", ShareDriver.Compose("hello", null, []));
    }
}
