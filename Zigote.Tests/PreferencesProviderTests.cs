using Xunit;
using Zigote.Core.State;
using Zigote.Persistence;
using Zigote.Persistence.SQLite;
using Zigote.Preferences;

namespace Zigote.Tests;

internal sealed class EditorTestPrefs : PreferencesProvider
{
    public EditorTestPrefs(PreferenceStore store) : base(store: store, prefix: "editor")
    {
        ShowGrid = Register(key: "showGrid", defaultValue: true);
        UiScale = Register(key: "uiScale", defaultValue: 1.0);
        Theme = Register(key: "theme", defaultValue: PrefTheme.Dark);
    }

    public Preference<bool> ShowGrid { get; }
    public Preference<double> UiScale { get; }
    public Preference<PrefTheme> Theme { get; }
}

internal sealed class GameplayTestPrefs : PreferencesProvider
{
    public GameplayTestPrefs(PreferenceStore store) : base(store: store, prefix: "gameplay")
    {
        Difficulty = Register(key: "difficulty", defaultValue: 2);
        MasterVolume = Register(key: "masterVolume", defaultValue: 0.8);
    }

    public Preference<int> Difficulty { get; }
    public Preference<double> MasterVolume { get; }
}

[Collection("Reactive-serial")] // preferences sit on the reactive graph's process-static state
public sealed class PreferencesProviderTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zigote-provider-tests");

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void Register_PrefixesKeys()
    {
        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var editor = new EditorTestPrefs(store);

        editor.ShowGrid.Value = false;

        Assert.Equal(expected: "editor.showGrid", actual: editor.ShowGrid.Key);
        Assert.True(backing.Contains("editor.showGrid"));
    }

    [Fact]
    public void Store_TracksProviders_InConstructionOrder()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var editor = new EditorTestPrefs(store);
        var gameplay = new GameplayTestPrefs(store);

        var providers = store.Providers;
        Assert.Equal(expected: 2, actual: providers.Count);
        Assert.Same(expected: editor, actual: providers[0]);
        Assert.Same(expected: gameplay, actual: providers[1]);
    }

    [Fact]
    public void Preferences_EnumerateInRegistrationOrder_WithTypes()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var editor = new EditorTestPrefs(store);

        Assert.Equal(
            expected: ["editor.showGrid", "editor.uiScale", "editor.theme"],
            actual: editor.Preferences.Select(p => p.Key)
        );
        Assert.Equal(
            expected: [typeof(bool), typeof(double), typeof(PrefTheme)],
            actual: editor.Preferences.Select(p => p.ValueType)
        );
    }

    [Fact]
    public void GenericEnumeration_DrivesUntypedSettingsUi()
    {
        // What a settings window does: enumerate providers and preferences it has never heard of,
        // observe them generically, and reset rows through IPreference.
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var editor = new EditorTestPrefs(store);

        var refreshed = new List<string>();
        var subscriptions = new List<IDisposable>();
        foreach (var provider in store.Providers)
        foreach (var preference in provider.Preferences)
        {
            string key = preference.Key;
            subscriptions.Add(preference.Observe(() => refreshed.Add(key)));
        }

        editor.UiScale.Value = 2.0;
        editor.Theme.Value = PrefTheme.Light;
        editor.UiScale.Reset();

        Assert.Equal(
            expected: ["editor.uiScale", "editor.theme", "editor.uiScale"],
            actual: refreshed
        );
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    [Fact]
    public void ProviderReset_OnlyTouchesItsOwnGroup()
    {
        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var editor = new EditorTestPrefs(store);
        var gameplay = new GameplayTestPrefs(store);

        editor.ShowGrid.Value = false;
        editor.UiScale.Value = 2.0;
        gameplay.Difficulty.Value = 5;

        editor.Reset();

        Assert.True(editor.ShowGrid.Value);
        Assert.Equal(expected: 1.0, actual: editor.UiScale.Value);
        Assert.False(editor.ShowGrid.IsSet);
        Assert.False(backing.Contains("editor.showGrid"));
        Assert.False(backing.Contains("editor.uiScale"));

        Assert.Equal(expected: 5, actual: gameplay.Difficulty.Value);
        Assert.True(gameplay.Difficulty.IsSet);
        Assert.True(backing.Contains("gameplay.difficulty"));
    }

    [Fact]
    public void ProviderReset_CoalescesIntoOneEffectPass()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var editor = new EditorTestPrefs(store);

        int runs = 0;
        using var effect = new Effect(() =>
            {
                _ = editor.ShowGrid.Value;
                _ = editor.UiScale.Value;
                runs++;
            }
        );
        Assert.Equal(expected: 1, actual: runs);

        editor.ShowGrid.Value = false; // 2
        editor.UiScale.Value = 2.0; // 3
        editor.Reset(); // both change back, but batched: 4, not 5

        Assert.Equal(expected: 4, actual: runs);
    }

    [Fact]
    public void ProviderReset_WhenNothingSet_IsSilent()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var editor = new EditorTestPrefs(store);

        int runs = 0;
        using var effect = new Effect(() =>
            {
                _ = editor.ShowGrid.Value;
                runs++;
            }
        );

        editor.Reset(); // every value already equals its default — no notifications

        Assert.Equal(expected: 1, actual: runs);
        Assert.False(editor.ShowGrid.IsSet);
    }

    [Fact]
    public void ResetAll_CoversEveryProvider()
    {
        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var editor = new EditorTestPrefs(store);
        var gameplay = new GameplayTestPrefs(store);

        editor.Theme.Value = PrefTheme.Light;
        gameplay.MasterVolume.Value = 0.2;

        store.ResetAll();

        Assert.Equal(expected: PrefTheme.Dark, actual: editor.Theme.Value);
        Assert.Equal(expected: 0.8, actual: gameplay.MasterVolume.Value);
        Assert.Empty(backing.Keys());
    }

    [Fact]
    public void Provider_RoundTrips_OnSqliteBackend()
    {
        string dbPath = Path.Combine(path1: _dir.FullName, path2: "prefs.db");

        using (var store = new PreferenceStore(new SqliteKeyValueStore(dbPath)))
        {
            var editor = new EditorTestPrefs(store);
            editor.UiScale.Value = 1.75;
            editor.Theme.Value = PrefTheme.Light;
            editor.ShowGrid.Reset(); // never set — stays absent
        }

        using var reopened = new PreferenceStore(new SqliteKeyValueStore(dbPath));
        var prefs = new EditorTestPrefs(reopened);

        Assert.Equal(expected: 1.75, actual: prefs.UiScale.Value);
        Assert.Equal(expected: PrefTheme.Light, actual: prefs.Theme.Value);
        Assert.True(prefs.UiScale.IsSet);
        Assert.False(prefs.ShowGrid.IsSet);
        Assert.True(prefs.ShowGrid.Value);
    }
}
