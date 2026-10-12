using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Zigote.UI.Host;

namespace Zigote.Tests;

/// <summary>
///     <c>App.PostDrainBudgetMs</c>: a flood of <c>App.Post</c> callbacks is spread over frames
///     instead of monopolising one, without losing or reordering any — and the default still drains
///     to empty, nested posts included.
/// </summary>
public class PostDrainBudgetTests
{
    // A real App needs a window and a GPU; Post/DrainPosted only touch the queue.
    private static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App)
            .GetField(name: "_posted", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(obj: app, value: new ConcurrentQueue<(Action Action, long Queued)>());
        return app;
    }

    [Fact]
    public void Default_DrainsToEmpty_IncludingNestedPosts()
    {
        var app = FakeApp();
        var order = new List<int>();
        app.Post(() =>
        {
            order.Add(1);
            app.Post(() => order.Add(3));
        });
        app.Post(() => order.Add(2));

        app.DrainPosted();

        Assert.Equal(expected: [1, 2, 3], actual: order);
        Assert.Equal(expected: 0, actual: app.PostedPending);
    }

    [Fact]
    public void Bounded_SpreadsAFloodAcrossFrames_InOrder_LosingNothing()
    {
        var app = FakeApp();
        app.PostDrainBudgetMs = 2;
        var order = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            int n = i;
            app.Post(() =>
            {
                Thread.Sleep(2); // each callback alone spends the whole budget
                order.Add(n);
            });
        }

        app.DrainPosted();
        Assert.Single(order);
        Assert.Equal(expected: 9, actual: app.PostedPending);

        for (int frame = 0; frame < 9; frame++) app.DrainPosted();
        Assert.Equal(expected: Enumerable.Range(start: 0, count: 10), actual: order);
        Assert.Equal(expected: 0, actual: app.PostedPending);
    }

    [Fact]
    public void Bounded_DefersCallbacksPostedDuringTheDrain()
    {
        var app = FakeApp();
        app.PostDrainBudgetMs = 10_000;
        int runs = 0;
        Action? repost = null;
        repost = () =>
        {
            runs++;
            app.Post(repost!);
        };
        app.Post(repost);

        app.DrainPosted(); // unbounded mode would never return

        Assert.Equal(expected: 1, actual: runs);
        Assert.Equal(expected: 1, actual: app.PostedPending);
    }

    [Fact]
    public void Bounded_ReportsWhatTheDrainDid()
    {
        var app = FakeApp();
        app.PostDrainBudgetMs = 1;
        app.Post(() => Thread.Sleep(5));
        app.Post(() => { });
        Thread.Sleep(3); // the first callback waits at least this long

        app.DrainPosted();

        var stats = app.LastPostDrain;
        Assert.Equal(expected: 1, actual: stats.Ran);
        Assert.Equal(expected: 1, actual: stats.Pending);
        Assert.True(stats.SlowestMs >= 5, $"slowest {stats.SlowestMs}");
        Assert.True(stats.ElapsedMs >= 5, $"elapsed {stats.ElapsedMs}");
        Assert.True(stats.OldestWaitMs >= 3, $"waited {stats.OldestWaitMs}");
    }

    [Fact]
    public void TryPost_RefusesPastCapacity_AndPostNeverDoes()
    {
        var app = FakeApp();
        app.PostCapacity = 2;
        int ran = 0;

        Assert.True(app.TryPost(() => ran++));
        Assert.True(app.TryPost(() => ran++));
        Assert.False(app.TryPost(() => ran++));
        app.Post(() => ran++); // not subject to the cap
        Assert.Equal(expected: 1, actual: app.PostRejected);

        app.DrainPosted();
        Assert.Equal(expected: 3, actual: ran);
        Assert.True(app.TryPost(() => ran++)); // room again
    }
}
