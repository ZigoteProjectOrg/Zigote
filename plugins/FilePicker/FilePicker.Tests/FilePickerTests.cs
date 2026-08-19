using Xunit;
using Zigote.Core.Engine;

namespace FilePicker.Tests;

/// <summary>
///     The desktop driver, through <see cref="FileDialog.ManagedBackend" /> — no engine in the test
///     process, so every request routes to the fake backend and no real dialog can open. Serial on
///     purpose: the backend is a process-wide static.
/// </summary>
public class FilePickerTests
{
    private static async Task<FileDialogRequest> Capture(
        Func<Task> call, params string[] reply)
    {
        FileDialogRequest? seen = null;
        FileDialog.ManagedBackend = request =>
        {
            seen = request;
            return Task.FromResult(reply);
        };
        try
        {
            await call();
        }
        finally
        {
            FileDialog.ManagedBackend = null;
        }

        Assert.NotNull(seen);
        return seen;
    }

    [Fact]
    public async Task OpenFile_MapsTitleFiltersAndKind()
    {
        string? picked = null;
        var request = await Capture(
            async () => picked = await FilePickerPlugin.OpenFileAsync(
                "Choose a song", [("Audio", ["mp3", ".flac"])]),
            "/music/a.mp3");

        Assert.Equal("/music/a.mp3", picked);
        Assert.Equal(FileDialogKind.OpenFile, request.Kind);
        Assert.Equal("Choose a song", request.Title);
        Assert.False(request.AllowMany);
        var filter = Assert.Single(request.Filters!);
        Assert.Equal("Audio", filter.Name);
        Assert.Equal(["mp3", ".flac"], filter.Extensions);
    }

    [Fact]
    public async Task OpenFiles_SetsAllowMany_AndReturnsAll()
    {
        string[] picked = [];
        var request = await Capture(
            async () => picked = await FilePickerPlugin.OpenFilesAsync(),
            "/a", "/b");

        Assert.True(request.AllowMany);
        Assert.Equal(["/a", "/b"], picked);
    }

    [Fact]
    public async Task PickFolder_UsesFolderKind_NullOnCancel()
    {
        string? picked = "sentinel";
        var request = await Capture(
            async () => picked = await FilePickerPlugin.PickFolderAsync("Music folder"));

        Assert.Equal(FileDialogKind.PickFolder, request.Kind);
        Assert.Equal("Music folder", request.Title);
        Assert.Null(picked); // empty backend reply = cancelled
    }

    [Fact]
    public async Task SaveFile_CarriesSuggestedName()
    {
        var request = await Capture(
            () => FilePickerPlugin.SaveFileAsync(
                "Export", suggestedName: "playlist.m3u", filters: [("Playlists", ["m3u"])]),
            "/out/playlist.m3u");

        Assert.Equal(FileDialogKind.SaveFile, request.Kind);
        Assert.Equal("playlist.m3u", request.FileName);
        Assert.Equal("Playlists", Assert.Single(request.Filters!).Name);
    }

    [Fact]
    public void Map_NullStaysNull()
    {
        Assert.Null(FilePickerDriver.Map(null));
    }
}
