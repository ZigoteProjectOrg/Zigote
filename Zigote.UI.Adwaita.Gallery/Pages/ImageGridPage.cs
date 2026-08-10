namespace AdwaitaGallery.Pages;

/// <summary>
///     Image Grid — a virtualized grid of network pictures that loads a page at a time as you reach
///     the bottom, and loads each picture only once it scrolls into view.
///     <para>
///         Three things keep it honest at feed scale. The grid is <b>virtualized</b>, so only the
///         visible rows exist; each tile is <b>lazy</b>, so a picture is fetched on the mount that
///         scrolling causes; and every fetch — the pictures and the API's own JSON — goes through
///         the shared network cache, which coalesces duplicates, gates concurrency and files the
///         answer on disk. Scroll back up and nothing is requested twice; restart the app and the
///         API is not called at all.
///     </para>
/// </summary>
public sealed class ImageGridPage : ComposedWidget
{
    // Three across works from the folded phone width to a wide window, which is the whole range
    // this shell has. A column count that follows the width would have to re-bind the grid from
    // inside a layout pass to change it.
    private const int Columns = 3;

    // A tile is ~200 px wide in this shell; 512 covers a retina one with room to spare and costs
    // 1 MB of GPU each, so a full dozen pages is ~150 MB rather than a gigabyte.
    private const uint TileMaxDim = 512;

    private readonly Signal<string?> _error = new(null);
    private readonly ListView _grid;
    private readonly List<ArtImage> _items = [];
    private readonly Signal<bool> _loading = new(false);
    private readonly Signal<int> _shown = new(0);

    // The feed hands back random picks, so pages overlap; a URL already held is dropped rather
    // than shown twice.
    private readonly HashSet<string> _urls = new(StringComparer.Ordinal);
    private int _cached;
    private int _page;

    public ImageGridPage()
    {
        _grid = GridView.Builder(
            Columns,
            0,
            Cell,
            Spacing.Md,
            Spacing.Md,
            0.72 // the sources are portraits, so portrait cells waste the least of each tile
        );
    }

    protected override void OnMount()
    {
        if (_items.Count == 0) RequestNextPage();
    }

    protected override Widget Build(BuildContext context)
    {
        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
            Children = {
                new Expanded(
                    new Padding(
                        EdgeInsets.Symmetric(Spacing.Lg, Spacing.Md),
                        new Stack {
                            Children = {
                                _grid,
                                new Align(
                                    Alignment.BottomCenter,
                                    new Padding(
                                        EdgeInsets.Only(bottom: Spacing.Lg),
                                        new Watch(Footer)
                                    )
                                ),
                            },
                        }
                    )
                ),
                new Watch(Status),
            },
        };
    }

    /// <summary>
    ///     Built only for cells the viewport can reach — which is what makes this the right place
    ///     to notice the end of the list, and what makes the tile's own mount the moment its
    ///     picture is fetched.
    /// </summary>
    private Widget Cell(int index)
    {
        // Posted, not called: this runs inside the layout pass, and appending a page re-binds the
        // grid's builder. One frame later is soon enough — the reader is still scrolling.
        if (index >= _items.Count - Columns) App.Active?.Post(RequestNextPage);
        return _items[index];
    }

    private void RequestNextPage()
    {
        if (_loading.Peek() || _page >= ArtSource.MaxPages) return;
        _loading.Value = true;
        _error.Value = null;
        _ = LoadPageAsync(_page);
    }

    private async Task LoadPageAsync(int page)
    {
        try
        {
            var pieces = await ArtSource.FetchPageAsync(page).ConfigureAwait(false);
            App.Active?.Post(() => Append(page, pieces));
        }
        catch (Exception error)
        {
            // Every path back to the widgets goes through the UI thread; nothing here touches a
            // signal from the worker.
            App.Active?.Post(() =>
                {
                    _error.Value = error is HttpRequestException or TaskCanceledException
                        ? "Couldn't reach the feed — check the connection"
                        : error.Message;
                    _loading.Value = false;
                }
            );
        }
    }

    private void Append(int page, ArtPiece[] pieces)
    {
        _page = page + 1;
        foreach (var piece in pieces)
        {
            if (!_urls.Add(piece.Url)) continue;
            var art = piece;
            var tile = new ArtImage(art, TileMaxDim) { OnPressed = () => ArtViewer.Show(art) };
            if (tile.WasCached) _cached++;
            _items.Add(tile);
        }

        _loading.Value = false;
        _shown.Value = _items.Count;

        // Re-point the existing grid instead of building a new one: a new grid is a new ListView,
        // and a new ListView starts at the top.
        GridView.Rebind(
            _grid,
            Columns,
            _items.Count,
            Cell,
            Spacing.Md,
            Spacing.Md,
            0.72
        );
    }

    private void Retry()
    {
        _error.Value = null;
        RequestNextPage();
        foreach (var tile in _items)
            if (tile.State.Peek() == ArtState.Failed)
                tile.Reload();
    }

    private Widget Footer()
    {
        if (_error.Value is { } message)
            return new AdwButton($"{message} — Try Again", Retry) { Pill = true };

        if (_loading.Value)
            return new DecoratedBox {
                Fill = Color.Rgba(0, 0, 0, 0.55f),
                Radius = AdwMetrics.Pill,
                Child = new Padding(
                    EdgeInsets.Symmetric(Spacing.Md, Spacing.Xs),
                    new Row(
                        spacing: Spacing.Sm,
                        mainAxisSize: MainAxisSize.Min,
                        crossAxisAlignment: CrossAxisAlignment.Center
                    ) {
                        Children = {
                            new AdwSpinner(16f),
                            new Label("Fetching more", AdwTypography.Caption, Color.White),
                        },
                    }
                ),
            };

        return SizedBox.Shrink();
    }

    private Widget Status()
    {
        var count = _shown.Value;
        var caption = count == 0
            ? "Scroll to the bottom to fetch the next page"
            : _page >= ArtSource.MaxPages
                ? $"End of the feed — {_cached} of {count} came straight off the disk"
                : $"Click a picture to zoom it · {_cached} of {count} came straight off the disk";

        return new Padding(
            EdgeInsets.Only(Spacing.Lg, 0f, Spacing.Lg, Spacing.Lg),
            Demo.Bar(Demo.Value($"{count} pictures"), Demo.Caption(caption))
        );
    }
}
