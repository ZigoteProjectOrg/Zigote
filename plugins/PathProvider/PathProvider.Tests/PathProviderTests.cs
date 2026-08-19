using Xunit;

namespace PathProvider.Tests;

public class PathProviderTests
{
    [Fact]
    public void Under_EnvWinsOverFallback()
    {
        Assert.Equal(
            Path.Combine("/custom/data", "myapp"),
            PathProviderDriver.Under("/custom/data", Path.Combine(".local", "share"), "myapp"));
    }

    [Fact]
    public void Under_FallsBackToHomeRelative()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(
            Path.Combine(home, ".cache", "myapp"),
            PathProviderDriver.Under(null, ".cache", "myapp"));
        Assert.Equal(
            Path.Combine(home, ".cache", "myapp"),
            PathProviderDriver.Under("", ".cache", "myapp"));
    }

    [Fact]
    public void ParseUserDir_UnquotesAndExpandsHome()
    {
        string?[] cases =
        [
            PathProviderDriver.ParseUserDir(
                ["# comment", "XDG_DOWNLOAD_DIR=\"$HOME/Загрузки\""], "XDG_DOWNLOAD_DIR", "/home/u"),
            PathProviderDriver.ParseUserDir(
                ["XDG_DOCUMENTS_DIR=\"/data/docs\""], "XDG_DOCUMENTS_DIR", "/home/u"),
            PathProviderDriver.ParseUserDir([], "XDG_DOWNLOAD_DIR", "/home/u"),
            PathProviderDriver.ParseUserDir(["XDG_DOWNLOAD_DIR=\"\""], "XDG_DOWNLOAD_DIR", "/home/u"),
        ];
        Assert.Equal("/home/u/Загрузки", cases[0]);
        Assert.Equal("/data/docs", cases[1]);
        Assert.Null(cases[2]);
        Assert.Null(cases[3]);
    }

    [Fact]
    public void AllPaths_AreRootedAndCarryAppName()
    {
        PathProviderPlugin.AppName = "ptest";
        string[] all =
        [
            PathProviderPlugin.Data(),
            PathProviderPlugin.Cache(),
            PathProviderPlugin.Config(),
            PathProviderPlugin.Temp(),
            PathProviderPlugin.Documents(),
            PathProviderPlugin.Downloads(),
        ];
        Assert.All(all, p => Assert.True(Path.IsPathRooted(p), $"not rooted: {p}"));
        Assert.EndsWith("ptest", PathProviderPlugin.Data());
        Assert.Equal(Path.Combine(PathProviderPlugin.Cache(), "covers"),
            PathProviderPlugin.Cache("covers"));
    }
}
