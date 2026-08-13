using Xunit;
using Zigote.Core.State;
using Zigote.Editor.Settings;
using Zigote.Persistence;
using Zigote.Preferences;

namespace Zigote.Tests;

[Collection("Reactive-serial")] // preferences sit on the reactive graph's process-static state
public sealed class ProjectHistoryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zigote-history-tests");

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException) { }
    }

    private static string Proj(string name) =>
        Path.GetFullPath($"/tmp/zigote-projects/{name}.zigoteproj");

    [Fact]
    public void RecordOpened_MostRecentFirst_DedupesAndTracksLast()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);

        history.RecordOpened(Proj("a"));
        history.RecordOpened(Proj("b"));
        history.RecordOpened(Proj("a")); // reopening moves it to the front, no duplicate

        Assert.Equal(expectedSpan: [Proj("a"), Proj("b")], actualArray: history.Recent.Value);
        Assert.Equal(expected: Proj("a"), actual: history.Last.Value);
    }

    [Fact]
    public void RecordOpened_CapsTheListAtTwelve()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);

        for (int i = 0; i < 20; i++) history.RecordOpened(Proj($"p{i}"));

        Assert.Equal(expected: 12, actual: history.Recent.Value.Length);
        Assert.Equal(expected: Proj("p19"), actual: history.Recent.Value[0]);
        Assert.Equal(expected: Proj("p8"), actual: history.Recent.Value[^1]);
    }

    [Fact]
    public void Forget_RemovesTheEntry_AndClearsAMatchingLast()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);
        history.RecordOpened(Proj("a"));
        history.RecordOpened(Proj("b"));

        history.Forget(Proj("b"));

        Assert.Equal(expectedSpan: [Proj("a")], actualArray: history.Recent.Value);
        Assert.Null(history.Last.Value); // "b" was also the last project

        history.Forget(Proj("a"));
        Assert.Empty(history.Recent.Value);
    }

    [Fact]
    public void ClearRecent_EmptiesTheList_ButKeepsLast()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);
        history.RecordOpened(Proj("a"));

        history.ClearRecent();

        Assert.Empty(history.Recent.Value);
        Assert.Equal(expected: Proj("a"), actual: history.Last.Value);
    }

    [Fact]
    public void RecordOpened_SameProjectAgain_IsANoOpWrite()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);
        history.RecordOpened(Proj("a"));

        int notifications = 0;
        using var subscription = history.Recent.Observe(() => notifications++);
        history.RecordOpened(Proj("a")); // structural comparer: same list → no persist, no notify

        Assert.Equal(expected: 0, actual: notifications);
    }

    [Fact]
    public void History_RoundTrips_AcrossStoreReopen()
    {
        string path = Path.Combine(path1: _dir.FullName, path2: "prefs.json");

        using (var store = new PreferenceStore(new JsonFileKeyValueStore(path)))
        {
            var history = new ProjectHistory(store);
            history.RecordOpened(Proj("a"));
            history.RecordOpened(Proj("b"));
        }

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(path));
        var restored = new ProjectHistory(reopened);

        Assert.Equal(expectedSpan: [Proj("b"), Proj("a")], actualArray: restored.Recent.Value);
        Assert.Equal(expected: Proj("b"), actual: restored.Last.Value);
    }
}
