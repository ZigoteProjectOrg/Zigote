using Xunit;
using Zigote.Core.State;
using Zigote.Persistence;
using Zigote.Preferences;

namespace Zigote.Tests;

/// <summary>Fails writes on demand so persistence atomicity can be asserted.</summary>
internal sealed class ThrowingKeyValueStore : IKeyValueStore
{
    private readonly InMemoryKeyValueStore _inner = new();

    public bool FailWrites { get; set; }

    public bool TryGet(string key, out string value) => _inner.TryGet(key: key, value: out value);

    public void Set(string key, string value)
    {
        if (FailWrites) throw new IOException("disk full");
        _inner.Set(key: key, value: value);
    }

    public bool Remove(string key) => _inner.Remove(key);

    public bool Contains(string key) => _inner.Contains(key);

    public IReadOnlyList<string> Keys() => _inner.Keys();

    public void Clear() => _inner.Clear();

    public void Flush() { }

    public void Dispose() => _inner.Dispose();
}

[Collection("Reactive-serial")] // shares the reactive graph's process-static state
public sealed class PreferenceComplexTests : IDisposable
{
    private readonly DirectoryInfo _dir =
        Directory.CreateTempSubdirectory("zigote-pref-complex-tests");

    private string FilePath => Path.Combine(path1: _dir.FullName, path2: "prefs.json");

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException) { }
    }

    // ---- failure atomicity -----------------------------------------------------------------

    [Fact]
    public void FailedWrite_LeavesSignalAndStorageUnchanged_ThenRecovers()
    {
        var throwing = new ThrowingKeyValueStore();
        using var store = new PreferenceStore(throwing);
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);
        scale.Value = 2.0;

        throwing.FailWrites = true;
        Assert.Throws<IOException>(() => scale.Value = 3.0);

        // Persist-before-notify: the failed write changed nothing anywhere.
        Assert.Equal(expected: 2.0, actual: scale.Value);
        Assert.True(throwing.TryGet(key: "ui.scale", value: out string raw));
        Assert.Equal(expected: "2", actual: raw);

        throwing.FailWrites = false;
        scale.Value = 3.0;
        Assert.Equal(expected: 3.0, actual: scale.Value);
        Assert.True(throwing.TryGet(key: "ui.scale", value: out raw));
        Assert.Equal(expected: "3", actual: raw);
    }

    [Fact]
    public void FailedWrite_DoesNotNotifySubscribers()
    {
        var throwing = new ThrowingKeyValueStore();
        using var store = new PreferenceStore(throwing);
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);

        var seen = new List<double>();
        using var subscription = scale.Subscribe(seen.Add);

        throwing.FailWrites = true;
        Assert.Throws<IOException>(() => scale.Value = 2.0);

        Assert.Equal(expected: [1.0], actual: seen); // only the immediate initial callback
        Assert.False(scale.IsSet);
    }

    // ---- reactive composition --------------------------------------------------------------

    [Fact]
    public void Batch_CoalescesWrites_AndComputedSeesNoGlitch()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var width = store.Preference(key: "window.width", defaultValue: 100);
        var height = store.Preference(key: "window.height", defaultValue: 50);
        using var area = Computed.From(() => width.Value * height.Value);

        var seen = new List<int>();
        using var effect = new Effect(() => seen.Add(area.Value));

        Reactive.Batch(() =>
            {
                width.Value = 200;
                height.Value = 100;
            }
        );

        // One settle, and never the glitch value 200 * 50 = 10_000.
        Assert.Equal(expected: [5_000, 20_000], actual: seen);
    }

    [Fact]
    public void EffectCascade_WritingOnePreferenceFromAnother_PersistsBoth()
    {
        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);
        var fontSize = store.Preference(key: "ui.fontSize", defaultValue: 0.0);

        // A derived preference kept in sync reactively — e.g. a legacy key mirrored from a new one.
        using var effect = new Effect(() => fontSize.Value = 12.0 * scale.Value);
        Assert.Equal(expected: 12.0, actual: fontSize.Value);

        scale.Value = 2.0;

        Assert.Equal(expected: 24.0, actual: fontSize.Value);
        Assert.True(backing.TryGet(key: "ui.scale", value: out string rawScale));
        Assert.True(backing.TryGet(key: "ui.fontSize", value: out string rawFont));
        Assert.Equal(expected: "2", actual: rawScale);
        Assert.Equal(expected: "24", actual: rawFont);
    }

    [Fact]
    public void ComputedOverPreference_StaysCoherent_AcrossResetAndRewrite()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);
        using var percent = Computed.From(() => $"{scale.Value * 100:0}%");

        Assert.Equal(expected: "100%", actual: percent.Value);
        scale.Value = 1.5;
        Assert.Equal(expected: "150%", actual: percent.Value);
        scale.Reset();
        Assert.Equal(expected: "100%", actual: percent.Value);
        scale.Value = 0.25;
        Assert.Equal(expected: "25%", actual: percent.Value);
    }

    [Fact]
    public void SubscriberChurn_DuringWrites_DoesNotCorruptNotification()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var counter = store.Preference(key: "stress.counter", defaultValue: 0);

        // A subscriber that disposes itself after three notifications, alongside a stable one.
        var stable = new List<int>();
        var shortLived = new List<int>();
        IDisposable? selfDisposing = null;
        using var stableSub = counter.Subscribe(stable.Add);
        selfDisposing = counter.Subscribe(v =>
            {
                shortLived.Add(v);
                if (shortLived.Count == 3) selfDisposing!.Dispose();
            }
        );

        for (int i = 1; i <= 5; i++) counter.Value = i;

        Assert.Equal(expected: [0, 1, 2, 3, 4, 5], actual: stable);
        Assert.Equal(expected: [0, 1, 2], actual: shortLived);
    }

    // ---- concurrency -----------------------------------------------------------------------

    [Fact]
    public async Task ParallelUpdates_LoseNoIncrements_AndStorageMatchesSignal()
    {
        const int Writers = 8;
        const int PerWriter = 200;

        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var counter = store.Preference(key: "stress.counter", defaultValue: 0);

        var tasks = Enumerable.Range(start: 0, count: Writers).Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < PerWriter; i++) counter.Update(x => x + 1);
                }
            )
        );
        await Task.WhenAll(tasks);

        Assert.Equal(expected: Writers * PerWriter, actual: counter.Value);
        Assert.True(backing.TryGet(key: "stress.counter", value: out string raw));
        Assert.Equal(expected: Writers * PerWriter, actual: int.Parse(raw));
    }

    [Fact]
    public async Task ParallelWrites_AcrossDistinctPreferences_AllPersistCorrectly()
    {
        const int Preferences = 16;
        const int WritesEach = 50;

        var backing = new InMemoryKeyValueStore();
        using var store = new PreferenceStore(backing);
        var prefs = Enumerable.Range(start: 0, count: Preferences)
            .Select(i => store.Preference(key: $"stress.p{i}", defaultValue: 0))
            .ToArray();

        var tasks = prefs.Select((pref, index) => Task.Run(() =>
                {
                    for (int i = 1; i <= WritesEach; i++) pref.Value = (index * 1_000) + i;
                }
            )
        );
        await Task.WhenAll(tasks);

        for (int i = 0; i < Preferences; i++)
        {
            int expected = (i * 1_000) + WritesEach;
            Assert.Equal(expected: expected, actual: prefs[i].Value);
            Assert.True(backing.TryGet(key: $"stress.p{i}", value: out string raw));
            Assert.Equal(expected: expected, actual: int.Parse(raw));
        }
    }

    [Fact]
    public async Task ConcurrentReadersViaComputed_SeeOnlyConsistentValues()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var value = store.Preference(key: "stress.value", defaultValue: 0);
        // The invariant: double is always exactly 2 × value, or the reader caught a torn state.
        using var doubled = Computed.From(() => (value.Value, value.Value * 2));

        int torn = 0;
        bool stop = false;
        var readers = Enumerable.Range(start: 0, count: 4).Select(_ => Task.Run(() =>
                {
                    while (!Volatile.Read(ref stop))
                    {
                        (int single, int twice) = doubled.Value;
                        if (twice != single * 2) Interlocked.Increment(ref torn);
                    }
                }
            )
        ).ToArray();

        for (int i = 1; i <= 500; i++) value.Value = i;
        Volatile.Write(location: ref stop, value: true);
        await Task.WhenAll(readers);

        Assert.Equal(expected: 0, actual: torn);
        Assert.Equal(expected: 500, actual: value.Value);
    }

    // ---- lifecycle across stores -----------------------------------------------------------

    [Fact]
    public void ResetAll_SurvivesReopen_AsFactoryDefaults()
    {
        using (var store = new PreferenceStore(new JsonFileKeyValueStore(FilePath)))
        {
            store.Preference(key: "ui.scale", defaultValue: 1.0).Value = 2.0;
            store.Preference(key: "editor.theme", defaultValue: PrefTheme.Dark).Value =
                PrefTheme.Light;
            store.ResetAll();
        }

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(FilePath));
        var scale = reopened.Preference(key: "ui.scale", defaultValue: 1.0);
        var theme = reopened.Preference(key: "editor.theme", defaultValue: PrefTheme.Dark);

        Assert.Equal(expected: 1.0, actual: scale.Value);
        Assert.Equal(expected: PrefTheme.Dark, actual: theme.Value);
        Assert.False(scale.IsSet);
        Assert.False(theme.IsSet);
    }

    [Fact]
    public void CorruptEntry_CanBeOverwritten_AndThenReloads()
    {
        using (var backing = new JsonFileKeyValueStore(FilePath))
        {
            backing.Set(key: "ui.scale", value: "definitely not json for a double");
            backing.Flush();
        }

        using (var store = new PreferenceStore(new JsonFileKeyValueStore(FilePath)))
        {
            var scale = store.Preference(key: "ui.scale", defaultValue: 1.0);
            Assert.Equal(expected: 1.0, actual: scale.Value); // corrupt → default, never throws
            Assert.False(scale.IsSet);
            scale.Value = 2.5; // healing write replaces the corrupt entry
        }

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(FilePath));
        var healed = reopened.Preference(key: "ui.scale", defaultValue: 1.0);
        Assert.Equal(expected: 2.5, actual: healed.Value);
        Assert.True(healed.IsSet);
    }

    [Fact]
    public void ManualFlushBackend_PersistsPreferencesOnStoreDispose()
    {
        using (var store =
               new PreferenceStore(new JsonFileKeyValueStore(path: FilePath, autoFlush: false)))
        {
            store.Preference(key: "ui.scale", defaultValue: 1.0).Value = 4.0;
            Assert.False(File.Exists(FilePath)); // buffered — nothing on disk yet
        } // PreferenceStore.Dispose → storage.Dispose → flush

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(FilePath));
        Assert.Equal(
            expected: 4.0,
            actual: reopened.Preference(key: "ui.scale", defaultValue: 1.0).Value
        );
    }
}
