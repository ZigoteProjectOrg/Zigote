using Xunit;
using Zigote.Persistence;
using Zigote.Persistence.SQLite;

namespace Zigote.Tests;

/// <summary>
///     The <see cref="IKeyValueStore" /> contract, run once per backend via the derived classes
///     below. Backend-specific behavior (durability across instances, corrupt-file quarantine,
///     table-name validation) lives on the derived classes.
/// </summary>
public abstract class KeyValueStoreContractTests : IDisposable
{
    private readonly DirectoryInfo _dir =
        Directory.CreateTempSubdirectory("zigote-persistence-tests");

    private IKeyValueStore? _store;

    protected string Root => _dir.FullName;

    protected IKeyValueStore Store => _store ??= Create();

    public void Dispose()
    {
        _store?.Dispose();
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }

    protected abstract IKeyValueStore Create();

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        Assert.False(Store.TryGet("missing", out _));
        Assert.False(Store.Contains("missing"));
    }

    [Fact]
    public void Set_Get_RoundTrips()
    {
        Store.Set("a", "hello");
        Assert.True(Store.TryGet("a", out var value));
        Assert.Equal("hello", value);
        Assert.True(Store.Contains("a"));
    }

    [Fact]
    public void Set_SameKey_Overwrites()
    {
        Store.Set("a", "one");
        Store.Set("a", "two");
        Assert.True(Store.TryGet("a", out var value));
        Assert.Equal("two", value);
    }

    [Fact]
    public void Values_RoundTrip_Verbatim()
    {
        // The contract says values are opaque: unicode, newlines, JSON, and empty strings survive.
        string[] payloads =
            ["", "  spaced  ", "line1\nline2", "{\"json\":true}", "émoji 🎮", "\"quoted\""];
        for (var i = 0; i < payloads.Length; i++) Store.Set($"k{i}", payloads[i]);
        for (var i = 0; i < payloads.Length; i++)
        {
            Assert.True(Store.TryGet($"k{i}", out var value));
            Assert.Equal(payloads[i], value);
        }
    }

    [Fact]
    public void Remove_ExistingKey_ReturnsTrue()
    {
        Store.Set("a", "x");
        Assert.True(Store.Remove("a"));
        Assert.False(Store.Contains("a"));
        Assert.False(Store.Remove("a"));
    }

    [Fact]
    public void Keys_ReturnsAllKeys_Sorted()
    {
        Store.Set("b", "2");
        Store.Set("a", "1");
        Store.Set("c", "3");
        Assert.Equal(["a", "b", "c"], Store.Keys());
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        Store.Set("a", "1");
        Store.Set("b", "2");
        Store.Clear();
        Assert.Empty(Store.Keys());
        Assert.False(Store.Contains("a"));
    }

    [Fact]
    public void EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => Store.Set("", "x"));
        Assert.Throws<ArgumentException>(() => Store.TryGet("", out _));
    }
}

public sealed class InMemoryKeyValueStoreTests : KeyValueStoreContractTests
{
    protected override IKeyValueStore Create()
    {
        return new InMemoryKeyValueStore();
    }
}

public sealed class JsonFileKeyValueStoreTests : KeyValueStoreContractTests
{
    private string FilePath => Path.Combine(Root, "store.json");

    protected override IKeyValueStore Create()
    {
        return new JsonFileKeyValueStore(FilePath);
    }

    [Fact]
    public void Values_Survive_Reopen()
    {
        Store.Set("a", "persisted");
        Store.Dispose();

        using var reopened = new JsonFileKeyValueStore(FilePath);
        Assert.True(reopened.TryGet("a", out var value));
        Assert.Equal("persisted", value);
    }

    [Fact]
    public void CorruptFile_IsQuarantined_StoreStartsEmpty()
    {
        File.WriteAllText(FilePath, "{ not json ]");

        using var store = new JsonFileKeyValueStore(FilePath);
        Assert.Empty(store.Keys());
        Assert.True(File.Exists(FilePath + ".corrupt"));

        // The store is usable and the next save replaces the corrupt file.
        store.Set("a", "fresh");
        using var reopened = new JsonFileKeyValueStore(FilePath);
        Assert.True(reopened.TryGet("a", out var value));
        Assert.Equal("fresh", value);
    }

    [Fact]
    public void ManualFlushMode_BuffersUntilFlush()
    {
        using var store = new JsonFileKeyValueStore(FilePath, false);
        store.Set("a", "buffered");
        Assert.False(File.Exists(FilePath));

        store.Flush();
        Assert.True(File.Exists(FilePath));

        using var reopened = new JsonFileKeyValueStore(FilePath);
        Assert.True(reopened.TryGet("a", out var value));
        Assert.Equal("buffered", value);
    }

    [Fact]
    public void Dispose_FlushesBufferedWrites()
    {
        var store = new JsonFileKeyValueStore(FilePath, false);
        store.Set("a", "on-dispose");
        store.Dispose();

        using var reopened = new JsonFileKeyValueStore(FilePath);
        Assert.True(reopened.TryGet("a", out var value));
        Assert.Equal("on-dispose", value);
    }

    [Fact]
    public void NoTmpFile_LeftBehind_AfterWrite()
    {
        Store.Set("a", "1");
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}

public sealed class SqliteKeyValueStoreTests : KeyValueStoreContractTests
{
    private string DbPath => Path.Combine(Root, "store.db");

    protected override IKeyValueStore Create()
    {
        return new SqliteKeyValueStore(DbPath);
    }

    [Fact]
    public void Values_Survive_Reopen()
    {
        Store.Set("a", "persisted");
        Store.Dispose();

        using var reopened = new SqliteKeyValueStore(DbPath);
        Assert.True(reopened.TryGet("a", out var value));
        Assert.Equal("persisted", value);
    }

    [Fact]
    public void InvalidTableName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SqliteKeyValueStore(DbPath, "bad name"));
        Assert.Throws<ArgumentException>(() => new SqliteKeyValueStore(DbPath, "1starts_with_digit")
        );
        Assert.Throws<ArgumentException>(() => new SqliteKeyValueStore(DbPath, "drop;table"));
        Assert.Throws<ArgumentException>(() => new SqliteKeyValueStore(DbPath, ""));
    }

    [Fact]
    public void TwoTables_ShareOneDatabase_Independently()
    {
        using var first = new SqliteKeyValueStore(DbPath, "first");
        using var second = new SqliteKeyValueStore(DbPath, "second");

        first.Set("shared.key", "from-first");
        second.Set("shared.key", "from-second");

        Assert.True(first.TryGet("shared.key", out var a));
        Assert.True(second.TryGet("shared.key", out var b));
        Assert.Equal("from-first", a);
        Assert.Equal("from-second", b);

        first.Clear();
        Assert.False(first.Contains("shared.key"));
        Assert.True(second.Contains("shared.key"));
    }
}
