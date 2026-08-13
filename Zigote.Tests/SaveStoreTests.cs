using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;
using Zigote.Save;

namespace Zigote.Tests;

public sealed record AotSaveState(string Name, int Score);

[JsonSerializable(typeof(AotSaveState))]
internal sealed partial class SaveTestJsonContext : JsonSerializerContext
{
}

public class SaveStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zigote-save-tests");

    private string Root => _dir.FullName;

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
    public void Write_Read_RoundTrips()
    {
        var store = new SaveStore(Root, 1);

        var write = store.Write("slot1", new PlayerV3("Ada", 90, 3));
        Assert.Equal(SaveStatus.Ok, write.Status);
        Assert.True(write.IsOk);
        Assert.Null(write.Error);

        var read = store.Read<PlayerV3>("slot1");
        Assert.Equal(SaveStatus.Ok, read.Status);
        Assert.Equal(new PlayerV3("Ada", 90, 3), read.State);
    }

    [Fact]
    public void Write_SameSlot_Overwrites()
    {
        var store = new SaveStore(Root, 1);
        store.Write("slot", new PlayerV1("First", 10));
        store.Write("slot", new PlayerV1("Second", 20));

        var read = store.Read<PlayerV1>("slot");
        Assert.Equal(SaveStatus.Ok, read.Status);
        Assert.Equal(new PlayerV1("Second", 20), read.State);
        Assert.Single(store.List());
    }

    [Fact]
    public void Write_EmitsTheVersionedEnvelope()
    {
        var store = new SaveStore(Root, 4);
        store.Write("fmt", new PlayerV1("A", 2));

        var envelope = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fmt.save")))!.AsObject();
        Assert.Equal(4, envelope["version"]!.GetValue<int>());
        Assert.True(envelope["savedAtUnixMs"]!.GetValue<long>() > 0);
        Assert.Equal("A", envelope["payload"]!["Name"]!.GetValue<string>());
        Assert.Equal(2, envelope["payload"]!["Hp"]!.GetValue<int>());
    }

    [Fact]
    public void Write_LeavesNoTmpFileBehind()
    {
        var store = new SaveStore(Root, 1);
        store.Write("slot", new PlayerV1("A", 1));

        Assert.True(File.Exists(Path.Combine(Root, "slot.save")));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
    }

    [Fact]
    public void Read_MigratesThroughTheChain_V1ToV3()
    {
        new SaveStore(Root, 1).Write("hero", new PlayerV1("Kay", 40));

        var reader = new SaveStore(Root, 3);
        reader.RegisterMigration(
            1,
            node =>
            {
                var o = node.AsObject();
                var hp = o["Hp"]!.GetValue<int>();
                o.Remove("Hp");
                o["Health"] = hp;
                return o;
            }
        );
        reader.RegisterMigration(
            2,
            node =>
            {
                var o = node.AsObject();
                o["Level"] = 1;
                return o;
            }
        );

        var read = reader.Read<PlayerV3>("hero");
        Assert.Equal(SaveStatus.Ok, read.Status);
        Assert.Equal(new PlayerV3("Kay", 40, 1), read.State);
    }

    [Fact]
    public void Read_MissingMigrationLink_ReportsMigrationMissing()
    {
        new SaveStore(Root, 1).Write("hero", new PlayerV1("Kay", 40));

        var reader = new SaveStore(Root, 3);
        reader.RegisterMigration(1, node => node); // 2→3 never registered

        var read = reader.Read<PlayerV3>("hero");
        Assert.Equal(SaveStatus.MigrationMissing, read.Status);
        Assert.Null(read.State);
        Assert.Contains("2", read.Error);
    }

    [Fact]
    public void Read_ThrowingMigration_ReportsMigrationFailed()
    {
        new SaveStore(Root, 1).Write("hero", new PlayerV1("Kay", 40));

        var reader = new SaveStore(Root, 2);
        reader.RegisterMigration(1, _ => throw new InvalidOperationException("boom"));

        var read = reader.Read<PlayerV1>("hero");
        Assert.Equal(SaveStatus.MigrationFailed, read.Status);
        Assert.Contains("boom", read.Error);
    }

    [Fact]
    public void Read_NewerEnvelope_ReportsFutureVersion()
    {
        new SaveStore(Root, 5).Write("hero", new PlayerV1("Kay", 40));

        var read = new SaveStore(Root, 3).Read<PlayerV1>("hero");
        Assert.Equal(SaveStatus.FutureVersion, read.Status);
        Assert.Null(read.State);
    }

    [Fact]
    public void Read_GarbageFile_ReportsCorrupt_AndListSkipsIt()
    {
        var store = new SaveStore(Root, 1);
        store.Write("good", new PlayerV1("A", 1));
        File.WriteAllText(Path.Combine(Root, "bad.save"), "{not json at all");
        File.WriteAllText(
            Path.Combine(Root, "shape.save"),
            "[1, 2, 3]"
        ); // valid JSON, wrong envelope

        Assert.Equal(SaveStatus.Corrupt, store.Read<PlayerV1>("bad").Status);
        Assert.Equal(SaveStatus.Corrupt, store.Read<PlayerV1>("shape").Status);

        var info = Assert.Single(store.List());
        Assert.Equal("good", info.Slot);
    }

    [Fact]
    public void Read_UnknownSlot_ReportsNotFound()
    {
        var store = new SaveStore(Root, 1);
        var read = store.Read<PlayerV1>("never-written");
        Assert.Equal(SaveStatus.NotFound, read.Status);
        Assert.Null(read.State);
    }

    [Fact]
    public void Delete_RemovesTheSlot_AndReportsExistence()
    {
        var store = new SaveStore(Root, 1);
        store.Write("s", new PlayerV1("A", 1));

        Assert.True(store.Exists("s"));
        Assert.True(store.Delete("s"));
        Assert.False(store.Exists("s"));
        Assert.False(store.Delete("s"));
        Assert.Equal(SaveStatus.NotFound, store.Read<PlayerV1>("s").Status);
    }

    [Fact]
    public void List_OrdersNewestFirst_WithSlotInfoFields()
    {
        var store = new SaveStore(Root, 7);
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);

        store.Write("older", new PlayerV1("A", 1));
        Thread.Sleep(30);
        store.Write("newer", new PlayerV1("B", 2));

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("newer", list[0].Slot);
        Assert.Equal("older", list[1].Slot);
        Assert.True(list[0].SavedAt >= list[1].SavedAt);
        Assert.All(
            list,
            info =>
            {
                Assert.Equal(7, info.Version);
                Assert.True(info.SizeBytes > 0);
                Assert.InRange(info.SavedAt, before, DateTimeOffset.UtcNow.AddSeconds(2));
            }
        );
    }

    [Fact]
    public void InvalidSlotNames_AreRejectedEverywhere()
    {
        var store = new SaveStore(Root, 1);
        string[] bad = ["a/b", "a\\b", "..", "../escape", "", "   ", "c:evil"];
        foreach (var slot in bad)
        {
            Assert.Equal(SaveStatus.InvalidSlot, store.Write(slot, new PlayerV1("X", 1)).Status);
            Assert.Equal(SaveStatus.InvalidSlot, store.Read<PlayerV1>(slot).Status);
            Assert.False(store.Exists(slot));
            Assert.False(store.Delete(slot));
        }

        Assert.Empty(Directory.GetFiles(Root));
        Assert.Empty(store.List());
    }

    [Fact]
    public void JsonTypeInfoOverloads_RoundTrip_AndShareTheEnvelope()
    {
        var store = new SaveStore(Root, 1);
        var state = new AotSaveState("Grace", 1200);

        var write = store.Write("aot", state, SaveTestJsonContext.Default.AotSaveState);
        Assert.Equal(SaveStatus.Ok, write.Status);

        var read = store.Read("aot", SaveTestJsonContext.Default.AotSaveState);
        Assert.Equal(SaveStatus.Ok, read.Status);
        Assert.Equal(state, read.State);

        // Same envelope either way: the reflection path reads what the source-generated path wrote.
        Assert.Equal(state, store.Read<AotSaveState>("aot").State);
    }

    private sealed record PlayerV1(string Name, int Hp);

    private sealed record PlayerV3(string Name, int Health, int Level);
}
