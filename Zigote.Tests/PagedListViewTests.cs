using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     The paging loop: pages arrive in order, an empty page ends the list, a failed page stops
///     the loop instead of retrying forever, and Retry picks up where it stopped.
/// </summary>
[Collection("Ticker")] // the widget polls its page on a ticker; AdvanceAll is process-wide
public class PagedListViewTests
{
    private static readonly Constraints Room = new(
        minWidth: 0f, maxWidth: 300f, minHeight: 0f, maxHeight: 300f);

    /// <summary>Mount, then let the ticker deliver whatever pages have completed.</summary>
    private static void Pump(PagedListView<int> list, int rounds = 3)
    {
        for (int i = 0; i < rounds; i++)
        {
            list.Measure(Room);
            list.Layout(Offset.Zero);
            Ticker.AdvanceAll(0.016f);
        }
    }

    private static PagedListView<int> Mount(
        Func<int, CancellationToken, Task<IReadOnlyList<int>>> fetch)
    {
        var list = new PagedListView<int>(fetch, (item, _) => new SizedBox(height: 10f));
        list.Attach(owner: null!, parent: null);
        return list;
    }

    [Fact]
    public void PagesLoadInOrderAndStopOnAnEmptyPage()
    {
        List<int> asked = [];
        var list = Mount((page, _) =>
        {
            asked.Add(page);
            IReadOnlyList<int> items = page < 2 ? [page * 10, (page * 10) + 1] : [];
            return Task.FromResult(items);
        });

        Pump(list, rounds: 6);

        Assert.Equal([0, 1, 2], asked);
        Assert.Equal([0, 1, 10, 11], list.Items);
        Assert.True(list.Ended);
        Assert.Null(list.Error);
    }

    [Fact]
    public void AFailedPageStopsTheLoop_AndRetryResumesFromIt()
    {
        int attempts = 0;
        var list = Mount((page, _) =>
        {
            attempts++;
            if (attempts == 1) return Task.FromException<IReadOnlyList<int>>(new IOException("offline"));
            IReadOnlyList<int> items = page == 0 ? [7] : [];
            return Task.FromResult(items);
        });

        Pump(list, rounds: 4);
        Assert.IsType<IOException>(list.Error);
        Assert.Empty(list.Items);
        int afterFailure = attempts;

        Pump(list, rounds: 3);
        Assert.Equal(afterFailure, attempts);   // stopped, not spinning on a dead connection

        list.Retry();
        Pump(list, rounds: 4);

        Assert.Null(list.Error);
        Assert.Equal([7], list.Items);
        Assert.True(list.Ended);
    }

    [Fact]
    public void RefreshStartsOver()
    {
        int calls = 0;
        var list = Mount((page, _) =>
        {
            calls++;
            IReadOnlyList<int> items = page == 0 ? [1, 2] : [];
            return Task.FromResult(items);
        });

        Pump(list, rounds: 4);
        Assert.Equal([1, 2], list.Items);

        list.Refresh();
        Pump(list, rounds: 4);

        Assert.Equal([1, 2], list.Items);   // loaded again, not appended to
        Assert.True(calls > 2);
    }
}
