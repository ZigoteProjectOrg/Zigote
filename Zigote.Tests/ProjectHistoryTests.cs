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
        catch (IOException)
        {
        }
    }

    private static string Proj(string name)
    {
        return Path.GetFullPath($"/tmp/zigote-projects/{name}.zigoteproj");
    }

    [Fact]
    public void RecordOpened_MostRecentFirst_DedupesAndTracksLast()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);

        history.RecordOpened(Proj("a"));
        history.RecordOpened(Proj("b"));
        history.RecordOpened(Proj("a")); // reopening moves it to the front, no duplicate

        Assert.Equal([Proj("a"), Proj("b")], history.Recent.Value);
        Assert.Equal(Proj("a"), history.Last.Value);
    }

    [Fact]
    public void RecordOpened_CapsTheListAtTwelve()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);

        for (var i = 0; i < 20; i++) history.RecordOpened(Proj($"p{i}"));

        Assert.Equal(12, history.Recent.Value.Length);
        Assert.Equal(Proj("p19"), history.Recent.Value[0]);
        Assert.Equal(Proj("p8"), history.Recent.Value[^1]);
    }

    [Fact]
    public void Forget_RemovesTheEntry_AndClearsAMatchingLast()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);
        history.RecordOpened(Proj("a"));
        history.RecordOpened(Proj("b"));

        history.Forget(Proj("b"));

        Assert.Equal([Proj("a")], history.Recent.Value);
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
        Assert.Equal(Proj("a"), history.Last.Value);
    }

    [Fact]
    public void RecordOpened_SameProjectAgain_IsANoOpWrite()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var history = new ProjectHistory(store);
        history.RecordOpened(Proj("a"));

        var notifications = 0;
        using var subscription = history.Recent.Observe(() => notifications++);
        history.RecordOpened(Proj("a")); // structural comparer: same list → no persist, no notify

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void History_RoundTrips_AcrossStoreReopen()
    {
        var path = Path.Combine(_dir.FullName, "prefs.json");

        using (var store = new PreferenceStore(new JsonFileKeyValueStore(path)))
        {
            var history = new ProjectHistory(store);
            history.RecordOpened(Proj("a"));
            history.RecordOpened(Proj("b"));
        }

        using var reopened = new PreferenceStore(new JsonFileKeyValueStore(path));
        var restored = new ProjectHistory(reopened);

        Assert.Equal([Proj("b"), Proj("a")], restored.Recent.Value);
        Assert.Equal(Proj("b"), restored.Last.Value);
    }
}