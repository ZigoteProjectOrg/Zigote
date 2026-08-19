using Zigote.Http;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Image Grid — a virtualized grid of network pictures that loads a page at a time as you reach
///     the bottom, and loads each picture only once it scrolls into view.
///     <para>
///         Three things keep it honest at feed scale. The grid is <b>virtualized</b>, so only the
///         visible rows exist; each tile is <b>lazy</b>, so a picture is fetched on the mount that
///         scrolling causes; and every fetch — the pictures and the API's own JSON — goes through
///         the app's one <see cref="ArtSource.Http">HttpRunner</see>, whose dedup coalesces
///         duplicates, whose per-host gate bounds the fan-out, and whose disk cache obeys the
///         origin: the pictures (eight-day <c>max-age</c> + ETag) come off the disk across
///         restarts, while the random feed listing is honestly refetched once per run.
///     </para>
///     <para>
///         The page's chrome — every credit chip, the fetch pill, the status toolbar the grid
///         scrolls under — is <b>Liquid Glass</b> (<see cref="LiquidPane" />), which also makes
///         this the engine's glass stress page: each pane is a render-pass break plus a
///         full-scene backdrop copy per frame, glass anywhere disables partial repaint, and a
///         screenful of tiles keeps a dozen-plus lenses refracting moving pictures while the
///         feed scrolls.
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

    // Room the grid keeps under its last row so the end of the feed can scroll clear of the
    // floating glass toolbar; mid-feed, tiles pass beneath the glass — that is the point.
    private const float DockClearance = 64f;

    private readonly Signal<string?> _error = new(null);
    private readonly ListView _grid;
    private readonly List<ArtImage> _items = [];
    private readonly Signal<bool> _loading = new(false);
    private readonly Signal<int> _shown = new(0);

    // The feed hands back random picks, so pages overlap; a URL already held is dropped rather
    // than shown twice.
    private readonly HashSet<string> _urls = new(StringComparer.Ordinal);
    private int _page;

    // Read in Build (registering the page as a theme dependent, so a theme flip rebuilds it and
    // the Watches below), then used by the Footer/Status builders — which run under a Watch,
    // where BuildContext.Current is not a reliable place to look the provider up.
    private ThemeData _theme = ThemeData.Dark;

    public ImageGridPage()
    {
        _grid = GridView.Builder(
            crossAxisCount: Columns,
            itemCount: 0,
            itemBuilder: Cell,
            mainAxisSpacing: Spacing.Md,
            crossAxisSpacing: Spacing.Md,
            childAspectRatio:
            0.72 // the sources are portraits, so portrait cells waste the least of each tile
        );
        _grid.Padding = EdgeInsets.Only(bottom: DockClearance);
    }

    protected override void OnMount()
    {
        if (_items.Count == 0) RequestNextPage();
    }

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);

        // The status readout is not furniture below the grid any more — it is a glass toolbar
        // floating over it, with the fetch pill stacking above. The pictures scroll straight
        // under both and refract through the lens, which is the Liquid Glass arrangement:
        // content everywhere, the functional layer floating on top.
        return new Padding(
            padding: EdgeInsets.Symmetric(horizontal: Spacing.Lg, vertical: Spacing.Md),
            child: new Stack {
                Children = {
                    _grid,
                    new Align(
                        alignment: Alignment.BottomCenter,
                        child: new Padding(
                            padding: EdgeInsets.Only(bottom: Spacing.Lg),
                            child: new Column(
                                spacing: Spacing.Sm,
                                mainAxisSize: MainAxisSize.Min,
                                crossAxisAlignment: CrossAxisAlignment.Center
                            ) {
                                Children = {
                                    new Watch(Footer),
                                    new Watch(Status),
                                },
                            }
                        )
                    ),
                },
            }
        );
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
        // The outcome is a value, so there is nothing to catch: one match, and every path back to
        // the widgets goes through the UI thread — nothing here touches a signal from the worker.
        var result = await ArtSource.FetchPageAsync(page).ConfigureAwait(false);
        App.Active?.Post(() =>
            {
                if (result.TryGet(out var pieces, out var error))
                {
                    Append(page: page, pieces: pieces);
                    return;
                }

                _error.Value = error switch {
                    HttpError.Transport or HttpError.Timeout =>
                        "Couldn't reach the feed — check the connection",
                    HttpError.Status status => $"The feed said {(int)status.Code}",
                    _ => error.Message,
                };
                _loading.Value = false;
            }
        );
    }

    private void Append(int page, ArtPiece[] pieces)
    {
        _page = page + 1;
        foreach (var piece in pieces)
        {
            if (!_urls.Add(piece.Url)) continue;
            var art = piece;
            _items.Add(
                new ArtImage(piece: art, maxDim: TileMaxDim) {
                    OnPressed = () => ArtViewer.Show(art),
                }
            );
        }

        _loading.Value = false;
        _shown.Value = _items.Count;

        // Re-point the existing grid instead of building a new one: a new grid is a new ListView,
        // and a new ListView starts at the top.
        GridView.Rebind(
            list: _grid,
            crossAxisCount: Columns,
            itemCount: _items.Count,
            itemBuilder: Cell,
            mainAxisSpacing: Spacing.Md,
            crossAxisSpacing: Spacing.Md,
            childAspectRatio: 0.72
        );
    }

    private void Retry()
    {
        _error.Value = null;
        RequestNextPage();
        foreach (var tile in _items)
        {
            if (tile.State.Peek() == ArtState.Failed)
                tile.Reload();
        }
    }

    private Widget Footer()
    {
        // Content on glass follows the pane's family, not the page palette — white on the dark
        // theme's dark glass, ink on the light theme's milky glass.
        var theme = _theme;
        if (_error.Value is { } message)
        {
            // An interactive pane: the gel response comes with LiquidPane.Interactive, so the glass
            // thickens under the pointer and compresses on the press — Liquid Glass as a button,
            // not just a backdrop.
            var pane = new LiquidPane {
                Elevation = 7f,
                Child = new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: Spacing.Lg, vertical: Spacing.Sm),
                    child: new Label(
                        text: $"{message} — Try Again",
                        style: AdwTypography.Caption,
                        color: LiquidPane.OnGlass(theme)
                    ) {
                        MaxLines = 1,
                        Overflow = TextOverflow.Ellipsis,
                    }
                ),
            };
            return LiquidPane.Interactive(
                pane: pane,
                onPressed: Retry,
                semantics: $"{message} — try again"
            );
        }

        if (_loading.Value)
        {
            return new LiquidPane {
                Elevation = 7f,
                Child = new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: Spacing.Md, vertical: Spacing.Xs),
                    child: new Row(
                        spacing: Spacing.Sm,
                        mainAxisSize: MainAxisSize.Min,
                        crossAxisAlignment: CrossAxisAlignment.Center
                    ) {
                        Children = {
                            new AdwSpinner(16f),
                            new Label(
                                text: "Fetching more",
                                style: AdwTypography.Caption,
                                color: LiquidPane.OnGlass(theme)
                            ),
                        },
                    }
                ),
            };
        }

        return SizedBox.Shrink();
    }

    private Widget Status()
    {
        // The disk readout comes from the runner itself — its OnLog counts every answer, so the
        // number covers exactly what went through the pipeline, pictures and listings alike.
        int count = _shown.Value;
        int fromDisk = Volatile.Read(ref ArtSource.CacheHits);
        string caption = count == 0
            ? "Scroll to the bottom to fetch the next page"
            : _page >= ArtSource.MaxPages
                ? $"End of the feed — {fromDisk} answers came straight off the disk"
                : $"Click a picture to zoom it · {fromDisk} answers came straight off the disk";

        // Content colour follows the pane's glass family (see LiquidPane.OnGlass): what is behind
        // this toolbar is whatever the feed served, and the pane's adaptive scrim keeps that
        // family legible over it.
        var theme = _theme;
        return new LiquidPane {
            Elevation = 0f,
            Adapt = 0.6f,
            Child = new Padding(
                padding: EdgeInsets.Symmetric(horizontal: Spacing.Lg, vertical: Spacing.Xl),
                child: new Row(
                    spacing: Spacing.Sm,
                    mainAxisSize: MainAxisSize.Min,
                    crossAxisAlignment: CrossAxisAlignment.Center
                ) {
                    Children = {
                        new Label(
                            text: $"{count} pictures",
                            style: AdwTypography.Monospace,
                            color: LiquidPane.OnGlassMuted(theme)
                        ),
                        new Label(
                            text: caption,
                            style: AdwTypography.Caption,
                            color: LiquidPane.OnGlassMuted(theme)
                        ) {
                            MaxLines = 1,
                            Overflow = TextOverflow.Ellipsis,
                        },
                    },
                }
            ),
        };
    }
}
