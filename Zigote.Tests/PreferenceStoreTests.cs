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
internal sealed partial class PreferenceTestJsonContext : JsonSerializerContext
{
}

/// <summary>Counts backend writes so equality gating can be asserted at the storage boundary.</summary>
internal sealed class CountingKeyValueStore : IKeyValueStore
{
    private readonly InMemoryKeyValueStore _inner = new();

    public int SetCount { get; private set; }

    public bool TryGet(string key, out string value)
    {
        return _inner.TryGet(key, out value);
    }

    public void Set(string key, string value)
    {
        SetCount++;
        _inner.Set(key, value);
    }

    public bool Remove(string key)
    {
        return _inner.Remove(key);
    }

    public bool Contains(string key)
    {
        return _inner.Contains(key);
    }

    public IReadOnlyList<string> Keys()
    {
        return _inner.Keys();
    }

    public void Clear()
    {
        _inner.Clear();
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}

[Collection("Reactive-serial")] // preferences sit on the reactive graph's process-static state
public sealed class PreferenceStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir =
        Directory.CreateTempSubdirectory("zigote-preferences-tests");

    private string FilePath => Path.Combine(_dir.FullName, "prefs.json");

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
    public void Unset_YieldsDefault_IsSetFalse()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var scale = store.Preference("ui.scale", 1.0);

        Assert.Equal(1.0, scale.Value);
        Assert.False(scale.IsSet);
        Assert.Equal(1.0, scale.Default);
    }

    [Fact]
    public void Set_WritesThrough_AndReloadsInFreshStore()
    {
        using (var store = new PreferenceStore(new JsonFileKeyValueStore(FilePath)))
        {
            store.Preference("editor.theme", PrefTheme.Dark).Value = PrefTheme.Light;
            store.Preference("ui.scale", 1.0).Value = 1.25;
        }

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(FilePath));
        var theme = reopened.Preference("editor.theme", PrefTheme.Dark);
        var scale = reopened.Preference("ui.scale", 1.0);

        Assert.Equal(PrefTheme.Light, theme.Value);
        Assert.Equal(1.25, scale.Value);
        Assert.True(theme.IsSet);
    }

    [Fact]
    public void SameKey_ReturnsSameInstance()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var first = store.Preference("a", 1);
        var second = store.Preference("a", 999);

        Assert.Same(first, second);
        Assert.Equal(1, second.Default); // the first call's default wins
    }

    [Fact]
    public void SameKey_DifferentType_Throws()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        _ = store.Preference("a", 1);

        Assert.Throws<InvalidOperationException>(() => store.Preference("a", "text"));
    }

    [Fact]
    public void Computed_TracksPreference()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var scale = store.Preference("ui.scale", 1.0);
        using var doubled = Computed.From(() => scale.Value * 2);

        Assert.Equal(2.0, doubled.Value);
        scale.Value = 1.5;
        Assert.Equal(3.0, doubled.Value);
    }

    [Fact]
    public void Subscribe_FiresImmediately_ThenOnChange()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var grid = store.Preference("editor.showGrid", true);

        var seen = new List<bool>();
        using var subscription = grid.Subscribe(seen.Add);
        grid.Value = false;
        grid.Value = false; // equality-gated: no second notification

        Assert.Equal([true, false], seen);
    }

    [Fact]
    public void UnchangedValue_DoesNotTouchStorage()
    {
        var counting = new CountingKeyValueStore();
        using var store = new PreferenceStore(counting);
        var scale = store.Preference("ui.scale", 1.0);

        scale.Value = 2.0;
        scale.Value = 2.0;
        scale.Value = 2.0;

        Assert.Equal(1, counting.SetCount);
    }

    [Fact]
    public void FirstSet_EqualToDefault_StillPersists()
    {
        var counting = new CountingKeyValueStore();
        using var store = new PreferenceStore(counting);
        var grid = store.Preference("editor.showGrid", true);

        grid.Value = true; // unchanged vs. default, but the user chose it

        Assert.Equal(1, counting.SetCount);
        Assert.True(grid.IsSet);
    }

    [Fact]
    public void Update_ReadsModifiesWrites()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var scale = store.Preference("ui.scale", 1.0);

        scale.Update(s => s + 0.5);
        scale.Update(s => s + 0.5);

        Assert.Equal(2.0, scale.Value);
        Assert.True(scale.IsSet);
    }

    [Fact]
    public void Reset_RemovesEntry_AndNotifies()
    {
        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var scale = store.Preference("ui.scale", 1.0);
        scale.Value = 2.0;
        Assert.True(backing.Contains("ui.scale"));

        var seen = new List<double>();
        using var subscription = scale.Subscribe(seen.Add);
        scale.Reset();

        Assert.Equal(1.0, scale.Value);
        Assert.False(scale.IsSet);
        Assert.False(backing.Contains("ui.scale"));
        Assert.Equal([2.0, 1.0], seen);
    }

    [Fact]
    public void CorruptPersistedValue_FallsBackToDefault_EntryLeftInPlace()
    {
        var backing = new InMemoryKeyValueStore();
        backing.Set("ui.scale", "not a number");

        using var store = new PreferenceStore(backing);
        var scale = store.Preference("ui.scale", 1.0);

        Assert.Equal(1.0, scale.Value);
        Assert.False(scale.IsSet);
        Assert.True(backing.Contains("ui.scale")); // quarantine-in-place, not deletion
    }

    [Fact]
    public void RecordValues_RoundTrip()
    {
        using (var store = new PreferenceStore(new JsonFileKeyValueStore(FilePath)))
        {
            var placement = store.Preference(
                "window.placement",
                new WindowPlacement(
                    0,
                    0,
                    1280,
                    720
                )
            );
            placement.Value = new WindowPlacement(
                50,
                60,
                1920,
                1080
            );
        }

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(FilePath));
        var reloaded = reopened.Preference(
            "window.placement",
            new WindowPlacement(
                0,
                0,
                1280,
                720
            )
        );

        Assert.Equal(
            new WindowPlacement(
                50,
                60,
                1920,
                1080
            ),
            reloaded.Value
        );
    }

    [Fact]
    public void JsonTypeInfo_Overload_RoundTrips()
    {
        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var placement = store.Preference(
            "window.placement",
            new WindowPlacement(
                0,
                0,
                1280,
                720
            ),
            PreferenceTestJsonContext.Default.WindowPlacement
        );

        placement.Value = new WindowPlacement(
            10,
            20,
            800,
            600
        );

        Assert.True(backing.TryGet("window.placement", out var raw));
        Assert.Contains("800", raw);
    }

    [Fact]
    public void ResetAll_ClearsStorage_IncludingUnmaterializedKeys()
    {
        var backing = new InMemoryKeyValueStore();
        backing.Set("orphan.key", "\"left over from an old run\"");

        using var store = new PreferenceStore(backing);
        var scale = store.Preference("ui.scale", 1.0);
        scale.Value = 3.0;

        store.ResetAll();

        Assert.Equal(1.0, scale.Value);
        Assert.False(scale.IsSet);
        Assert.Empty(backing.Keys());
    }

    [Fact]
    public void Effect_ReactsToPreferenceChange()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var grid = store.Preference("editor.showGrid", true);

        var runs = 0;
        using var effect = new Effect(() =>
            {
                _ = grid.Value;
                runs++;
            }
        );

        Assert.Equal(1, runs);
        grid.Value = false;
        Assert.Equal(2, runs);
    }
}