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
        string? spec = FileDialog.BuildFilterSpec(
            [
                new FileDialogFilter(name: "Zigote Project", "zigoteproj"),
                new FileDialogFilter(name: "Images", "png", "jpg"),
            ]
        );
        Assert.Equal(expected: "Zigote Project|zigoteproj\nImages|png;jpg", actual: spec);
    }

    [Fact]
    public void FilterSpec_NormalizesDotAndStarPrefixes()
    {
        string? spec = FileDialog.BuildFilterSpec(
            [
                new FileDialogFilter(
                    name: "Images",
                    ".png",
                    "*.jpg",
                    "webp"
                ),
            ]
        );
        Assert.Equal(expected: "Images|png;jpg;webp", actual: spec);
    }

    [Fact]
    public void FilterSpec_StarCollapsesToAllFiles()
    {
        // A "*" (or "*.*") extension makes the whole filter an all-files pattern — SDL only
        // accepts "*" as a standalone pattern, never mixed with extensions.
        string? spec = FileDialog.BuildFilterSpec(
            [
                new FileDialogFilter(name: "All Files", "png", "*"),
            ]
        );
        Assert.Equal(expected: "All Files|*", actual: spec);

        string? starDotStar =
            FileDialog.BuildFilterSpec([new FileDialogFilter(name: "All", "*.*")]);
        Assert.Equal(expected: "All|*", actual: starDotStar);
    }

    [Fact]
    public void FilterSpec_SanitizesReservedCharactersInNames()
    {
        // '|' and '\n' are the spec's own separators; they must never leak in from a name.
        string? spec = FileDialog.BuildFilterSpec(
            [
                new FileDialogFilter(name: "Weird|Name\nHere", "png"),
            ]
        );
        Assert.Equal(expected: "Weird Name Here|png", actual: spec);
    }

    [Fact]
    public void NativeExports_ResolveAndReportIdle()
    {
        // Exercises the real zigote_file_dialog_* symbols (name + calling convention) without
        // showing UI: supported is compile-time, and status/consume are safe while idle.
        Assert.True(NativeEngine.FileDialogSupported()); // all desktop OSes have a backend
        Assert.Equal(
            expected: 0,
            actual: NativeEngine.FileDialogStatus()
        ); // idle before any request
        NativeEngine.FileDialogConsume(); // no-op when idle — must not throw or corrupt state
        Assert.Equal(expected: 0, actual: NativeEngine.FileDialogStatus());
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
        bool prevEnabled = FileDialog.Enabled;
        try
        {
            FileDialog.Enabled = false;
            FileDialogRequest? seen = null;
            FileDialog.ManagedBackend = request =>
            {
                seen = request;
                return Task.FromResult<string[]>(["/tmp/picked.scene"]);
            };

            string? result = await FileDialog.SaveFileAsync(
                title: "Save Scene As",
                startDirectory: "/tmp",
                suggestedName: "level.scene",
                filters: [new FileDialogFilter(name: "Zigote Scene", "scene")]
            );

            Assert.Equal(expected: "/tmp/picked.scene", actual: result);
            Assert.NotNull(seen);
            Assert.Equal(expected: FileDialogKind.SaveFile, actual: seen!.Kind);
            Assert.Equal(expected: "level.scene", actual: seen.FileName);
            Assert.Equal(expected: "/tmp", actual: seen.Directory);
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
        bool prevEnabled = FileDialog.Enabled;
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
