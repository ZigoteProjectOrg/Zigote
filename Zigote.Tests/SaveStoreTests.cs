using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;
using Zigote.Save;

namespace Zigote.Tests;

public sealed record AotSaveState(string Name, int Score);

[JsonSerializable(typeof(AotSaveState))]
internal sealed partial class SaveTestJsonContext : JsonSerializerContext { }

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
        catch (IOException) { }
    }

    [Fact]
    public void Write_Read_RoundTrips()
    {
        var store = new SaveStore(directory: Root, currentVersion: 1);

        var write = store.Write(
            slot: "slot1",
            state: new PlayerV3(Name: "Ada", Health: 90, Level: 3)
        );
        Assert.Equal(expected: SaveStatus.Ok, actual: write.Status);
        Assert.True(write.IsOk);
        Assert.Null(write.Error);

        var read = store.Read<PlayerV3>("slot1");
        Assert.Equal(expected: SaveStatus.Ok, actual: read.Status);
        Assert.Equal(expected: new PlayerV3(Name: "Ada", Health: 90, Level: 3), actual: read.State);
    }

    [Fact]
    public void Write_SameSlot_Overwrites()
    {
        var store = new SaveStore(directory: Root, currentVersion: 1);
        store.Write(slot: "slot", state: new PlayerV1(Name: "First", Hp: 10));
        store.Write(slot: "slot", state: new PlayerV1(Name: "Second", Hp: 20));

        var read = store.Read<PlayerV1>("slot");
        Assert.Equal(expected: SaveStatus.Ok, actual: read.Status);
        Assert.Equal(expected: new PlayerV1(Name: "Second", Hp: 20), actual: read.State);
        Assert.Single(store.List());
    }

    [Fact]
    public void Write_EmitsTheVersionedEnvelope()
    {
        var store = new SaveStore(directory: Root, currentVersion: 4);
        store.Write(slot: "fmt", state: new PlayerV1(Name: "A", Hp: 2));

        var envelope =
            JsonNode.Parse(File.ReadAllText(Path.Combine(path1: Root, path2: "fmt.save")))!
                .AsObject();
        Assert.Equal(expected: 4, actual: envelope["version"]!.GetValue<int>());
        Assert.True(envelope["savedAtUnixMs"]!.GetValue<long>() > 0);
        Assert.Equal(expected: "A", actual: envelope["payload"]!["Name"]!.GetValue<string>());
        Assert.Equal(expected: 2, actual: envelope["payload"]!["Hp"]!.GetValue<int>());
    }

    [Fact]
    public void Write_LeavesNoTmpFileBehind()
    {
        var store = new SaveStore(directory: Root, currentVersion: 1);
        store.Write(slot: "slot", state: new PlayerV1(Name: "A", Hp: 1));

        Assert.True(File.Exists(Path.Combine(path1: Root, path2: "slot.save")));
        Assert.Empty(Directory.GetFiles(path: Root, searchPattern: "*.tmp"));
    }

    [Fact]
    public void Read_MigratesThroughTheChain_V1ToV3()
    {
        new SaveStore(directory: Root, currentVersion: 1).Write(
            slot: "hero",
            state: new PlayerV1(Name: "Kay", Hp: 40)
        );

        var reader = new SaveStore(directory: Root, currentVersion: 3);
        reader.RegisterMigration(
            fromVersion: 1,
            migrate: node =>
            {
                var o = node.AsObject();
                int hp = o["Hp"]!.GetValue<int>();
                o.Remove("Hp");
                o["Health"] = hp;
                return o;
            }
        );
        reader.RegisterMigration(
            fromVersion: 2,
            migrate: node =>
            {
                var o = node.AsObject();
                o["Level"] = 1;
                return o;
            }
        );

        var read = reader.Read<PlayerV3>("hero");
        Assert.Equal(expected: SaveStatus.Ok, actual: read.Status);
        Assert.Equal(expected: new PlayerV3(Name: "Kay", Health: 40, Level: 1), actual: read.State);
    }

    [Fact]
    public void Read_MissingMigrationLink_ReportsMigrationMissing()
    {
        new SaveStore(directory: Root, currentVersion: 1).Write(
            slot: "hero",
            state: new PlayerV1(Name: "Kay", Hp: 40)
        );

        var reader = new SaveStore(directory: Root, currentVersion: 3);
        reader.RegisterMigration(fromVersion: 1, migrate: node => node); // 2→3 never registered

        var read = reader.Read<PlayerV3>("hero");
        Assert.Equal(expected: SaveStatus.MigrationMissing, actual: read.Status);
        Assert.Null(read.State);
        Assert.Contains(expectedSubstring: "2", actualString: read.Error);
    }

    [Fact]
    public void Read_ThrowingMigration_ReportsMigrationFailed()
    {
        new SaveStore(directory: Root, currentVersion: 1).Write(
            slot: "hero",
            state: new PlayerV1(Name: "Kay", Hp: 40)
        );

        var reader = new SaveStore(directory: Root, currentVersion: 2);
        reader.RegisterMigration(
            fromVersion: 1,
            migrate: _ => throw new InvalidOperationException("boom")
        );

        var read = reader.Read<PlayerV1>("hero");
        Assert.Equal(expected: SaveStatus.MigrationFailed, actual: read.Status);
        Assert.Contains(expectedSubstring: "boom", actualString: read.Error);
    }

    [Fact]
    public void Read_NewerEnvelope_ReportsFutureVersion()
    {
        new SaveStore(directory: Root, currentVersion: 5).Write(
            slot: "hero",
            state: new PlayerV1(Name: "Kay", Hp: 40)
        );

        var read = new SaveStore(directory: Root, currentVersion: 3).Read<PlayerV1>("hero");
        Assert.Equal(expected: SaveStatus.FutureVersion, actual: read.Status);
        Assert.Null(read.State);
    }

    [Fact]
    public void Read_GarbageFile_ReportsCorrupt_AndListSkipsIt()
    {
        var store = new SaveStore(directory: Root, currentVersion: 1);
        store.Write(slot: "good", state: new PlayerV1(Name: "A", Hp: 1));
        File.WriteAllText(
            path: Path.Combine(path1: Root, path2: "bad.save"),
            contents: "{not json at all"
        );
        File.WriteAllText(
            path: Path.Combine(path1: Root, path2: "shape.save"),
            contents: "[1, 2, 3]"
        ); // valid JSON, wrong envelope

        Assert.Equal(expected: SaveStatus.Corrupt, actual: store.Read<PlayerV1>("bad").Status);
        Assert.Equal(expected: SaveStatus.Corrupt, actual: store.Read<PlayerV1>("shape").Status);

        var info = Assert.Single(store.List());
        Assert.Equal(expected: "good", actual: info.Slot);
    }

    [Fact]
    public void Read_UnknownSlot_ReportsNotFound()
    {
        var store = new SaveStore(directory: Root, currentVersion: 1);
        var read = store.Read<PlayerV1>("never-written");
        Assert.Equal(expected: SaveStatus.NotFound, actual: read.Status);
        Assert.Null(read.State);
    }

    [Fact]
    public void Delete_RemovesTheSlot_AndReportsExistence()
    {
        var store = new SaveStore(directory: Root, currentVersion: 1);
        store.Write(slot: "s", state: new PlayerV1(Name: "A", Hp: 1));

        Assert.True(store.Exists("s"));
        Assert.True(store.Delete("s"));
        Assert.False(store.Exists("s"));
        Assert.False(store.Delete("s"));
        Assert.Equal(expected: SaveStatus.NotFound, actual: store.Read<PlayerV1>("s").Status);
    }

    [Fact]
    public void List_OrdersNewestFirst_WithSlotInfoFields()
    {
        var store = new SaveStore(directory: Root, currentVersion: 7);
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);

        store.Write(slot: "older", state: new PlayerV1(Name: "A", Hp: 1));
        Thread.Sleep(30);
        store.Write(slot: "newer", state: new PlayerV1(Name: "B", Hp: 2));

        var list = store.List();
        Assert.Equal(expected: 2, actual: list.Count);
        Assert.Equal(expected: "newer", actual: list[0].Slot);
        Assert.Equal(expected: "older", actual: list[1].Slot);
        Assert.True(list[0].SavedAt >= list[1].SavedAt);
        Assert.All(
            collection: list,
            action: info =>
            {
                Assert.Equal(expected: 7, actual: info.Version);
                Assert.True(info.SizeBytes > 0);
                Assert.InRange(
                    actual: info.SavedAt,
                    low: before,
                    high: DateTimeOffset.UtcNow.AddSeconds(2)
                );
            }
        );
    }

    [Fact]
    public void InvalidSlotNames_AreRejectedEverywhere()
    {
        var store = new SaveStore(directory: Root, currentVersion: 1);
        string[] bad = ["a/b", "a\\b", "..", "../escape", "", "   ", "c:evil"];
        foreach (string slot in bad)
        {
            Assert.Equal(
                expected: SaveStatus.InvalidSlot,
                actual: store.Write(slot: slot, state: new PlayerV1(Name: "X", Hp: 1)).Status
            );
            Assert.Equal(
                expected: SaveStatus.InvalidSlot,
                actual: store.Read<PlayerV1>(slot).Status
            );
            Assert.False(store.Exists(slot));
            Assert.False(store.Delete(slot));
        }

        Assert.Empty(Directory.GetFiles(Root));
        Assert.Empty(store.List());
    }

    [Fact]
    public void JsonTypeInfoOverloads_RoundTrip_AndShareTheEnvelope()
    {
        var store = new SaveStore(directory: Root, currentVersion: 1);
        var state = new AotSaveState(Name: "Grace", Score: 1200);

        var write = store.Write(
            slot: "aot",
            state: state,
            typeInfo: SaveTestJsonContext.Default.AotSaveState
        );
        Assert.Equal(expected: SaveStatus.Ok, actual: write.Status);

        var read = store.Read(slot: "aot", typeInfo: SaveTestJsonContext.Default.AotSaveState);
        Assert.Equal(expected: SaveStatus.Ok, actual: read.Status);
        Assert.Equal(expected: state, actual: read.State);

        // Same envelope either way: the reflection path reads what the source-generated path wrote.
        Assert.Equal(expected: state, actual: store.Read<AotSaveState>("aot").State);
    }

    private sealed record PlayerV1(string Name, int Hp);

    private sealed record PlayerV3(string Name, int Health, int Level);
}
