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
        catch (IOException) { }
    }

    protected abstract IKeyValueStore Create();

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        Assert.False(Store.TryGet(key: "missing", value: out _));
        Assert.False(Store.Contains("missing"));
    }

    [Fact]
    public void Set_Get_RoundTrips()
    {
        Store.Set(key: "a", value: "hello");
        Assert.True(Store.TryGet(key: "a", value: out string value));
        Assert.Equal(expected: "hello", actual: value);
        Assert.True(Store.Contains("a"));
    }

    [Fact]
    public void Set_SameKey_Overwrites()
    {
        Store.Set(key: "a", value: "one");
        Store.Set(key: "a", value: "two");
        Assert.True(Store.TryGet(key: "a", value: out string value));
        Assert.Equal(expected: "two", actual: value);
    }

    [Fact]
    public void Values_RoundTrip_Verbatim()
    {
        // The contract says values are opaque: unicode, newlines, JSON, and empty strings survive.
        string[] payloads =
            ["", "  spaced  ", "line1\nline2", "{\"json\":true}", "émoji 🎮", "\"quoted\""];
        for (int i = 0; i < payloads.Length; i++) Store.Set(key: $"k{i}", value: payloads[i]);
        for (int i = 0; i < payloads.Length; i++)
        {
            Assert.True(Store.TryGet(key: $"k{i}", value: out string value));
            Assert.Equal(expected: payloads[i], actual: value);
        }
    }

    [Fact]
    public void Remove_ExistingKey_ReturnsTrue()
    {
        Store.Set(key: "a", value: "x");
        Assert.True(Store.Remove("a"));
        Assert.False(Store.Contains("a"));
        Assert.False(Store.Remove("a"));
    }

    [Fact]
    public void Keys_ReturnsAllKeys_Sorted()
    {
        Store.Set(key: "b", value: "2");
        Store.Set(key: "a", value: "1");
        Store.Set(key: "c", value: "3");
        Assert.Equal(expected: ["a", "b", "c"], actual: Store.Keys());
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        Store.Set(key: "a", value: "1");
        Store.Set(key: "b", value: "2");
        Store.Clear();
        Assert.Empty(Store.Keys());
        Assert.False(Store.Contains("a"));
    }

    [Fact]
    public void EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => Store.Set(key: "", value: "x"));
        Assert.Throws<ArgumentException>(() => Store.TryGet(key: "", value: out _));
    }
}

public sealed class InMemoryKeyValueStoreTests : KeyValueStoreContractTests
{
    protected override IKeyValueStore Create() => new InMemoryKeyValueStore();
}

public sealed class JsonFileKeyValueStoreTests : KeyValueStoreContractTests
{
    private string FilePath => Path.Combine(path1: Root, path2: "store.json");

    protected override IKeyValueStore Create() => new JsonFileKeyValueStore(FilePath);

    [Fact]
    public void Values_Survive_Reopen()
    {
        Store.Set(key: "a", value: "persisted");
        Store.Dispose();

        using var reopened = new JsonFileKeyValueStore(FilePath);
        Assert.True(reopened.TryGet(key: "a", value: out string value));
        Assert.Equal(expected: "persisted", actual: value);
    }

    [Fact]
    public void CorruptFile_IsQuarantined_StoreStartsEmpty()
    {
        File.WriteAllText(path: FilePath, contents: "{ not json ]");

        using var store = new JsonFileKeyValueStore(FilePath);
        Assert.Empty(store.Keys());
        Assert.True(File.Exists(FilePath + ".corrupt"));

        // The store is usable and the next save replaces the corrupt file.
        store.Set(key: "a", value: "fresh");
        using var reopened = new JsonFileKeyValueStore(FilePath);
        Assert.True(reopened.TryGet(key: "a", value: out string value));
        Assert.Equal(expected: "fresh", actual: value);
    }

    [Fact]
    public void ManualFlushMode_BuffersUntilFlush()
    {
        using var store = new JsonFileKeyValueStore(path: FilePath, autoFlush: false);
        store.Set(key: "a", value: "buffered");
        Assert.False(File.Exists(FilePath));

        store.Flush();
        Assert.True(File.Exists(FilePath));

        using var reopened = new JsonFileKeyValueStore(FilePath);
        Assert.True(reopened.TryGet(key: "a", value: out string value));
        Assert.Equal(expected: "buffered", actual: value);
    }

    [Fact]
    public void Dispose_FlushesBufferedWrites()
    {
        var store = new JsonFileKeyValueStore(path: FilePath, autoFlush: false);
        store.Set(key: "a", value: "on-dispose");
        store.Dispose();

        using var reopened = new JsonFileKeyValueStore(FilePath);
        Assert.True(reopened.TryGet(key: "a", value: out string value));
        Assert.Equal(expected: "on-dispose", actual: value);
    }

    [Fact]
    public void NoTmpFile_LeftBehind_AfterWrite()
    {
        Store.Set(key: "a", value: "1");
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}

public sealed class SqliteKeyValueStoreTests : KeyValueStoreContractTests
{
    private string DbPath => Path.Combine(path1: Root, path2: "store.db");

    protected override IKeyValueStore Create() => new SqliteKeyValueStore(DbPath);

    [Fact]
    public void Values_Survive_Reopen()
    {
        Store.Set(key: "a", value: "persisted");
        Store.Dispose();

        using var reopened = new SqliteKeyValueStore(DbPath);
        Assert.True(reopened.TryGet(key: "a", value: out string value));
        Assert.Equal(expected: "persisted", actual: value);
    }

    [Fact]
    public void InvalidTableName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SqliteKeyValueStore(
                path: DbPath,
                tableName: "bad name"
            )
        );
        Assert.Throws<ArgumentException>(() => new SqliteKeyValueStore(
                path: DbPath,
                tableName: "1starts_with_digit"
            )
        );
        Assert.Throws<ArgumentException>(() => new SqliteKeyValueStore(
                path: DbPath,
                tableName: "drop;table"
            )
        );
        Assert.Throws<ArgumentException>(() => new SqliteKeyValueStore(path: DbPath, tableName: "")
        );
    }

    [Fact]
    public void TwoTables_ShareOneDatabase_Independently()
    {
        using var first = new SqliteKeyValueStore(path: DbPath, tableName: "first");
        using var second = new SqliteKeyValueStore(path: DbPath, tableName: "second");

        first.Set(key: "shared.key", value: "from-first");
        second.Set(key: "shared.key", value: "from-second");

        Assert.True(first.TryGet(key: "shared.key", value: out string a));
        Assert.True(second.TryGet(key: "shared.key", value: out string b));
        Assert.Equal(expected: "from-first", actual: a);
        Assert.Equal(expected: "from-second", actual: b);

        first.Clear();
        Assert.False(first.Contains("shared.key"));
        Assert.True(second.Contains("shared.key"));
    }
}
