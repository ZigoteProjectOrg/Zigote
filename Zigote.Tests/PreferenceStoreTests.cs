using System.Text.Json.Serialization;
using Xunit;
using Zigote.Core.State;
using Zigote.Persistence;
using Zigote.Preferences;

namespace Zigote.Tests;

public enum PrefTheme
{
    Dark,
    Light,
}

public sealed record WindowPlacement(int X, int Y, int Width, int Height);

[JsonSerializable(typeof(WindowPlacement))]
internal sealed partial class PreferenceTestJsonContext : JsonSerializerContext { }

/// <summary>Counts backend writes so equality gating can be asserted at the storage boundary.</summary>
internal sealed class CountingKeyValueStore : IKeyValueStore
{
    private readonly InMemoryKeyValueStore _inner = new();

    public int SetCount { get; private set; }

    public bool TryGet(string key, out string value) => _inner.TryGet(key: key, value: out value);

    public void Set(string key, string value)
    {
        SetCount++;
        _inner.Set(key: key, value: value);
    }

    public bool Remove(string key) => _inner.Remove(key);

    public bool Contains(string key) => _inner.Contains(key);

    public IReadOnlyList<string> Keys() => _inner.Keys();

    public void Clear() => _inner.Clear();

    public void Flush() { }

    public void Dispose() => _inner.Dispose();
}

[Collection("Reactive-serial")] // preferences sit on the reactive graph's process-static state
public sealed class PreferenceStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir =
        Directory.CreateTempSubdirectory("zigote-preferences-tests");

    private string FilePath => Path.Combine(path1: _dir.FullName, path2: "prefs.json");

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void Unset_YieldsDefault_IsSetFalse()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);

        Assert.Equal(expected: 1.0, actual: scale.Value);
        Assert.False(scale.IsSet);
        Assert.Equal(expected: 1.0, actual: scale.Default);
    }

    [Fact]
    public void Set_WritesThrough_AndReloadsInFreshStore()
    {
        using (var store = new PreferenceStore(new JsonFileKeyValueStore(FilePath)))
        {
            store.Preference(key: "editor.theme", defaultValue: PrefTheme.Dark).Value =
                PrefTheme.Light;
            store.Preference(key: "ui.scale", defaultValue: 1.0).Value = 1.25;
        }

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(FilePath));
        var theme = reopened.Preference(key: "editor.theme", defaultValue: PrefTheme.Dark);
        var scale = reopened.Preference(key: "ui.scale", defaultValue: 1.0);

        Assert.Equal(expected: PrefTheme.Light, actual: theme.Value);
        Assert.Equal(expected: 1.25, actual: scale.Value);
        Assert.True(theme.IsSet);
    }

    [Fact]
    public void SameKey_ReturnsSameInstance()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var first = store.Preference(key: "a", defaultValue: 1);
        var second = store.Preference(key: "a", defaultValue: 999);

        Assert.Same(expected: first, actual: second);
        Assert.Equal(expected: 1, actual: second.Default); // the first call's default wins
    }

    [Fact]
    public void SameKey_DifferentType_Throws()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        _ = store.Preference(key: "a", defaultValue: 1);

        Assert.Throws<InvalidOperationException>(() => store.Preference(
                key: "a",
                defaultValue: "text"
            )
        );
    }

    [Fact]
    public void Computed_TracksPreference()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);
        using var doubled = Computed.From(() => scale.Value * 2);

        Assert.Equal(expected: 2.0, actual: doubled.Value);
        scale.Value = 1.5;
        Assert.Equal(expected: 3.0, actual: doubled.Value);
    }

    [Fact]
    public void Subscribe_FiresImmediately_ThenOnChange()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var grid = store.Preference(key: "editor.showGrid", defaultValue: true);

        var seen = new List<bool>();
        using var subscription = grid.Subscribe(seen.Add);
        grid.Value = false;
        grid.Value = false; // equality-gated: no second notification

        Assert.Equal(expected: [true, false], actual: seen);
    }

    [Fact]
    public void UnchangedValue_DoesNotTouchStorage()
    {
        var counting = new CountingKeyValueStore();
        using var store = new PreferenceStore(counting);
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);

        scale.Value = 2.0;
        scale.Value = 2.0;
        scale.Value = 2.0;

        Assert.Equal(expected: 1, actual: counting.SetCount);
    }

    [Fact]
    public void FirstSet_EqualToDefault_StillPersists()
    {
        var counting = new CountingKeyValueStore();
        using var store = new PreferenceStore(counting);
        var grid = store.Preference(key: "editor.showGrid", defaultValue: true);

        grid.Value = true; // unchanged vs. default, but the user chose it

        Assert.Equal(expected: 1, actual: counting.SetCount);
        Assert.True(grid.IsSet);
    }

    [Fact]
    public void Update_ReadsModifiesWrites()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);

        scale.Update(s => s + 0.5);
        scale.Update(s => s + 0.5);

        Assert.Equal(expected: 2.0, actual: scale.Value);
        Assert.True(scale.IsSet);
    }

    [Fact]
    public void Reset_RemovesEntry_AndNotifies()
    {
        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);
        scale.Value = 2.0;
        Assert.True(backing.Contains("ui.scale"));

        var seen = new List<double>();
        using var subscription = scale.Subscribe(seen.Add);
        scale.Reset();

        Assert.Equal(expected: 1.0, actual: scale.Value);
        Assert.False(scale.IsSet);
        Assert.False(backing.Contains("ui.scale"));
        Assert.Equal(expected: [2.0, 1.0], actual: seen);
    }

    [Fact]
    public void CorruptPersistedValue_FallsBackToDefault_EntryLeftInPlace()
    {
        var backing = new InMemoryKeyValueStore();
        backing.Set(key: "ui.scale", value: "not a number");

        using var store = new PreferenceStore(backing);
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);

        Assert.Equal(expected: 1.0, actual: scale.Value);
        Assert.False(scale.IsSet);
        Assert.True(backing.Contains("ui.scale")); // quarantine-in-place, not deletion
    }

    [Fact]
    public void RecordValues_RoundTrip()
    {
        using (var store = new PreferenceStore(new JsonFileKeyValueStore(FilePath)))
        {
            var placement = store.Preference(
                key: "window.placement",
                defaultValue: new WindowPlacement(
                    X: 0,
                    Y: 0,
                    Width: 1280,
                    Height: 720
                )
            );
            placement.Value = new WindowPlacement(
                X: 50,
                Y: 60,
                Width: 1920,
                Height: 1080
            );
        }

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(FilePath));
        var reloaded = reopened.Preference(
            key: "window.placement",
            defaultValue: new WindowPlacement(
                X: 0,
                Y: 0,
                Width: 1280,
                Height: 720
            )
        );

        Assert.Equal(
            expected: new WindowPlacement(
                X: 50,
                Y: 60,
                Width: 1920,
                Height: 1080
            ),
            actual: reloaded.Value
        );
    }

    [Fact]
    public void JsonTypeInfo_Overload_RoundTrips()
    {
        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var placement = store.Preference(
            key: "window.placement",
            defaultValue: new WindowPlacement(
                X: 0,
                Y: 0,
                Width: 1280,
                Height: 720
            ),
            typeInfo: PreferenceTestJsonContext.Default.WindowPlacement
        );

        placement.Value = new WindowPlacement(
            X: 10,
            Y: 20,
            Width: 800,
            Height: 600
        );

        Assert.True(backing.TryGet(key: "window.placement", value: out string raw));
        Assert.Contains(expectedSubstring: "800", actualString: raw);
    }

    [Fact]
    public void ResetAll_ClearsStorage_IncludingUnmaterializedKeys()
    {
        var backing = new InMemoryKeyValueStore();
        backing.Set(key: "orphan.key", value: "\"left over from an old run\"");

        using var store = new PreferenceStore(backing);
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);
        scale.Value = 3.0;

        store.ResetAll();

        Assert.Equal(expected: 1.0, actual: scale.Value);
        Assert.False(scale.IsSet);
        Assert.Empty(backing.Keys());
    }

    [Fact]
    public void Effect_ReactsToPreferenceChange()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var grid = store.Preference(key: "editor.showGrid", defaultValue: true);

        int runs = 0;
        using var effect = new Effect(() =>
            {
                _ = grid.Value;
                runs++;
            }
        );

        Assert.Equal(expected: 1, actual: runs);
        grid.Value = false;
        Assert.Equal(expected: 2, actual: runs);
    }
}
