using Xunit;
using Zigote.Core.Engine;
using Zigote.Core.Native;

namespace Zigote.Tests;

/// <summary>
///     Pins the filter-spec encoding <see cref="FileDialog" /> sends across the FFI
///     (newline-separated "Name|pattern" lines, SDL pattern form — see
///     Zigote.Engine/docs/file-dialogs.md). The native dialog itself is OS-owned UI and is
///     covered by manual smoke tests only; these run without a native library.
/// </summary>
public class FileDialogTests
{
    [Fact]
    public void FilterSpec_JoinsNameAndExtensions()
    {
        var spec = FileDialog.BuildFilterSpec([
            new FileDialogFilter("Zigote Project", "zigoteproj"),
            new FileDialogFilter("Images", "png", "jpg"),
        ]);
        Assert.Equal("Zigote Project|zigoteproj\nImages|png;jpg", spec);
    }

    [Fact]
    public void FilterSpec_NormalizesDotAndStarPrefixes()
    {
        var spec = FileDialog.BuildFilterSpec([
            new FileDialogFilter("Images", ".png", "*.jpg", "webp"),
        ]);
        Assert.Equal("Images|png;jpg;webp", spec);
    }

    [Fact]
    public void FilterSpec_StarCollapsesToAllFiles()
    {
        // A "*" (or "*.*") extension makes the whole filter an all-files pattern — SDL only
        // accepts "*" as a standalone pattern, never mixed with extensions.
        var spec = FileDialog.BuildFilterSpec([
            new FileDialogFilter("All Files", "png", "*"),
        ]);
        Assert.Equal("All Files|*", spec);

        var starDotStar = FileDialog.BuildFilterSpec([new FileDialogFilter("All", "*.*")]);
        Assert.Equal("All|*", starDotStar);
    }

    [Fact]
    public void FilterSpec_SanitizesReservedCharactersInNames()
    {
        // '|' and '\n' are the spec's own separators; they must never leak in from a name.
        var spec = FileDialog.BuildFilterSpec([
            new FileDialogFilter("Weird|Name\nHere", "png"),
        ]);
        Assert.Equal("Weird Name Here|png", spec);
    }

    [Fact]
    public void NativeExports_ResolveAndReportIdle()
    {
        // Exercises the real zigote_file_dialog_* symbols (name + calling convention) without
        // showing UI: supported is compile-time, and status/consume are safe while idle.
        Assert.True(NativeEngine.FileDialogSupported()); // all desktop OSes have a backend
        Assert.Equal(0, NativeEngine.FileDialogStatus()); // idle before any request
        NativeEngine.FileDialogConsume(); // no-op when idle — must not throw or corrupt state
        Assert.Equal(0, NativeEngine.FileDialogStatus());
    }

    [Fact]
    public void FilterSpec_EmptyInputsProduceNull()
    {
        Assert.Null(FileDialog.BuildFilterSpec(null));
        Assert.Null(FileDialog.BuildFilterSpec([]));
        // A filter with no extensions is dropped rather than emitting a malformed line.
        Assert.Null(FileDialog.BuildFilterSpec([new FileDialogFilter("Nothing")]));
    }

    [Fact]
    public async Task ManagedBackend_ReceivesStructuredRequest_WhenNativeUnavailable()
    {
        // No engine runs under test, so IsSupported is false and every call must route to the
        // managed backend with the request intact — the same path the in-app browser serves.
        var prevBackend = FileDialog.ManagedBackend;
        var prevEnabled = FileDialog.Enabled;
        try
        {
            FileDialog.Enabled = false;
            FileDialogRequest? seen = null;
            FileDialog.ManagedBackend = request =>
            {
                seen = request;
                return Task.FromResult<string[]>(["/tmp/picked.scene"]);
            };

            var result = await FileDialog.SaveFileAsync(
                "Save Scene As",
                "/tmp",
                "level.scene",
                [new FileDialogFilter("Zigote Scene", "scene")]
            );

            Assert.Equal("/tmp/picked.scene", result);
            Assert.NotNull(seen);
            Assert.Equal(FileDialogKind.SaveFile, seen!.Kind);
            Assert.Equal("level.scene", seen.FileName);
            Assert.Equal("/tmp", seen.Directory);
            Assert.Single(seen.Filters!);
        }
        finally
        {
            FileDialog.ManagedBackend = prevBackend;
            FileDialog.Enabled = prevEnabled;
        }
    }

    [Fact]
    public async Task NoBackendAtAll_FaultsWithFileDialogException()
    {
        var prevBackend = FileDialog.ManagedBackend;
        var prevEnabled = FileDialog.Enabled;
        try
        {
            FileDialog.Enabled = false;
            FileDialog.ManagedBackend = null;
            await Assert.ThrowsAsync<FileDialogException>(() => FileDialog.OpenFileAsync());
        }
        finally
        {
            FileDialog.ManagedBackend = prevBackend;
            FileDialog.Enabled = prevEnabled;
        }
    }
}
