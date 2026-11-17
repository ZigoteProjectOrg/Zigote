using Xunit;
using Zigote.Core.IO;

namespace Zigote.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(path1: Path.GetTempPath(), path2: "zigote-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(path: _root, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void Write_ReplacesTheTarget_AndLeavesNoTemporaryFile()
    {
        string path = Path.Combine(path1: _root, path2: "scene.json");
        AtomicFile.WriteAllText(path: path, contents: "one");
        AtomicFile.WriteAllText(path: path, contents: "two — ünïcode");

        Assert.Equal(expected: "two — ünïcode", actual: File.ReadAllText(path));
        Assert.Equal(expected: ["scene.json"], actual: Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void AFailedWrite_LeavesThePreviousContentIntact()
    {
        // The target is a directory entry that cannot be replaced by a file: the rename fails
        // after the new content was written, which is the moment a truncating write has already
        // destroyed the old one.
        string path = Path.Combine(path1: _root, path2: "scene.json");
        AtomicFile.WriteAllText(path: path, contents: "authored");

        string blocked = Path.Combine(path1: _root, path2: "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(path: Path.Combine(path1: blocked, path2: "keep"), contents: "x");
        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path: blocked, contents: "new"));

        Assert.Equal(expected: "authored", actual: File.ReadAllText(path));
        Assert.True(Directory.Exists(blocked));
        Assert.DoesNotContain(Directory.GetFiles(_root), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
