using Zigote.UI.Widgets.Controls;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     A <see cref="ListView" /> that loads itself, page by page, as the user reaches the end —
///     the <c>infinite_scroll_pagination</c> shape. Every app writes this loop; the loop is
///     always the same, so it lives here.
///     <code>
///   new PagedListView&lt;Post&gt;(
///       fetchPage:   (page, ct) => api.PostsAsync(page, ct),
///       itemBuilder: (post, _) => new PostTile(post),
///       itemHeight:  72f)
/// </code>
///     <para>
///         A page that comes back empty ends the list — no "has more" flag to keep in step. A
///         page that throws stops the loop and shows a footer that retries on tap, because
///         retrying forever on a dead connection is how a phone's battery disappears.
///     </para>
/// </summary>
/// <typeparam name="T">The item type the pages are made of.</typeparam>
public sealed class PagedListView<T> : ComposedWidget
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<T>>> _fetchPage;
    private readonly Func<T, int, Widget> _itemBuilder;
    private readonly List<T> _items = [];
    private readonly ListView _list;
    private bool _end;
    private Exception? _error;
    private int _page;
    private Task<IReadOnlyList<T>>? _pending;

    /// <param name="fetchPage">Loads page <c>n</c> (zero-based). Return an empty page to end the list.</param>
    /// <param name="itemBuilder">Builds the row for an item and its index.</param>
    /// <param name="itemHeight">Row height — the list virtualises against it.</param>
    /// <param name="triggerDistance">How close to the end, in pixels, starts the next page.</param>
    public PagedListView(
        Func<int, CancellationToken, Task<IReadOnlyList<T>>> fetchPage,
        Func<T, int, Widget> itemBuilder,
        float itemHeight = 56f,
        float triggerDistance = 400f)
    {
        _fetchPage = fetchPage;
        _itemBuilder = itemBuilder;
        TriggerDistance = triggerDistance;
        _list = new ListView(itemExtent: itemHeight);
        _list.OnScrolled = (offset, max) =>
        {
            if (max - offset <= TriggerDistance) LoadNextPage();
        };
    }

    /// <summary>How close to the end of the list the next page starts loading.</summary>
    public float TriggerDistance { get; set; }

    /// <summary>The list underneath — set padding, scroll speed or a per-row height on it.</summary>
    public ListView List => _list;

    /// <summary>Everything loaded so far, in page order.</summary>
    public IReadOnlyList<T> Items => _items;

    /// <summary>True once a page has come back empty: there is nothing more to load.</summary>
    public bool Ended => _end;

    /// <summary>The error that stopped the loop, or null. Cleared by <see cref="Retry" />.</summary>
    public Exception? Error => _error;

    /// <summary>Shown under the last row while a page is loading. Defaults to a shimmering row.</summary>
    public Func<Widget>? LoadingFooter { get; set; }

    /// <summary>Shown under the last row when a page failed; tapping the widget should call <see cref="Retry" />.</summary>
    public Func<Exception, Widget>? ErrorFooter { get; set; }

    /// <summary>Drop everything and load from page zero again — a pull-to-refresh, a changed filter.</summary>
    public void Refresh()
    {
        _items.Clear();
        _page = 0;
        _end = false;
        _error = null;
        _pending = null;
        _list.OffsetY = 0f;
        Rebuild();
        LoadNextPage();
    }

    /// <summary>Clear the error and try the failed page again.</summary>
    public void Retry()
    {
        if (_error is null) return;
        _error = null;
        Rebuild();
        LoadNextPage();
    }

    protected override void OnMount()
    {
        CreateTicker(_ => Poll()).Start();
        LoadNextPage();
    }

    public override void Detach()
    {
        // The page in flight outlives the widget otherwise, and lands on a list nobody can see.
        _cancellation.Cancel();
        base.Detach();
    }

    protected override Widget Build(BuildContext context)
    {
        int footers = Footer() is null ? 0 : 1;
        _list.SetBuilder(
            _items.Count + footers,
            index => index < _items.Count
                ? _itemBuilder(_items[index], index)
                : Footer() ?? new SizedBox(),
            keepScroll: true);
        return _list;
    }

    /// <summary>The row after the last item: a placeholder while loading, a retry when failed, nothing when done.</summary>
    private Widget? Footer()
    {
        if (_error is { } error)
            return ErrorFooter is { } build
                ? build(error)
                : new GestureDetector(
                    new Label($"Could not load — tap to retry"),
                    onTap: Retry);
        if (_end) return null;
        return LoadingFooter?.Invoke() ?? new Skeleton();
    }

    private void LoadNextPage()
    {
        if (_pending is not null || _end || _error is not null) return;
        if (_cancellation.IsCancellationRequested) return;

        try
        {
            _pending = _fetchPage(_page, _cancellation.Token);
        }
        catch (Exception e)
        {
            // A fetcher that throws synchronously fails the same way as one that faults.
            _error = e;
            Rebuild();
        }
    }

    /// <summary>
    ///     Watched on the UI thread by a ticker, the way <see cref="FutureBuilder{T}" /> watches
    ///     its task: the page completes on a worker, and the list is only ever touched here.
    /// </summary>
    private void Poll()
    {
        if (_pending is not { IsCompleted: true } finished) return;
        _pending = null;

        if (finished.IsFaulted)
        {
            _error = finished.Exception?.InnerException ?? finished.Exception;
        }
        else if (finished.IsCanceled)
        {
            _end = true;   // cancelled with the widget; nothing more will arrive
        }
        else
        {
            var page = finished.Result;
            if (page.Count == 0)
            {
                _end = true;
            }
            else
            {
                _items.AddRange(page);
                _page++;
            }
        }

        Rebuild();
    }

    private void Rebuild() => Invalidate();
}
