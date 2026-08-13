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
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Is_A_Safe_NoOp_Without_A_Store()
    {
        GameSave.Store = null;
        try
        {
            Assert.False(GameSave.IsAvailable);
            Assert.Equal(SaveStatus.IoError, GameSave.Write("slot", new Progress(1, 100f)).Status);
            Assert.Equal(SaveStatus.NotFound, GameSave.Read<Progress>("slot").Status);
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
        GameSave.Store = new SaveStore(GameSave.DefaultDirectory!, 2);
        try
        {
            Assert.True(GameSave.IsAvailable);
            Assert.Equal(SaveStatus.Ok, GameSave.Write("run", new Progress(5, 62.5f)).Status);
            Assert.True(GameSave.Exists("run"));

            var read = GameSave.Read<Progress>("run");
            Assert.Equal(SaveStatus.Ok, read.Status);
            Assert.Equal(new Progress(5, 62.5f), read.State);

            var info = Assert.Single(GameSave.List());
            Assert.Equal("run", info.Slot);
            Assert.Equal(2, info.Version);

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
