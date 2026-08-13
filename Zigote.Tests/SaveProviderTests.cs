using Xunit;
using Zigote.Save;
using GameSave = Zigote.Scripting.Save;

namespace Zigote.Tests;

public class SaveProviderTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zigote-save-tests");

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void Is_A_Safe_NoOp_Without_A_Store()
    {
        GameSave.Store = null;
        try
        {
            Assert.False(GameSave.IsAvailable);
            Assert.Equal(
                expected: SaveStatus.IoError,
                actual: GameSave.Write(slot: "slot", state: new Progress(Level: 1, Health: 100f))
                    .Status
            );
            Assert.Equal(
                expected: SaveStatus.NotFound,
                actual: GameSave.Read<Progress>("slot").Status
            );
            Assert.False(GameSave.Exists("slot"));
            Assert.False(GameSave.Delete("slot"));
            Assert.Empty(GameSave.List());
        }
        finally
        {
            GameSave.Store = null;
        }
    }

    [Fact]
    public void Routes_Through_The_Assigned_Store()
    {
        GameSave.DefaultDirectory = _dir.FullName;
        GameSave.Store = new SaveStore(directory: GameSave.DefaultDirectory!, currentVersion: 2);
        try
        {
            Assert.True(GameSave.IsAvailable);
            Assert.Equal(
                expected: SaveStatus.Ok,
                actual: GameSave.Write(slot: "run", state: new Progress(Level: 5, Health: 62.5f))
                    .Status
            );
            Assert.True(GameSave.Exists("run"));

            var read = GameSave.Read<Progress>("run");
            Assert.Equal(expected: SaveStatus.Ok, actual: read.Status);
            Assert.Equal(expected: new Progress(Level: 5, Health: 62.5f), actual: read.State);

            var info = Assert.Single(GameSave.List());
            Assert.Equal(expected: "run", actual: info.Slot);
            Assert.Equal(expected: 2, actual: info.Version);

            Assert.True(GameSave.Delete("run"));
            Assert.False(GameSave.Exists("run"));
        }
        finally
        {
            GameSave.Store = null;
            GameSave.DefaultDirectory = null;
        }
    }

    private sealed record Progress(int Level, float Health);
}
