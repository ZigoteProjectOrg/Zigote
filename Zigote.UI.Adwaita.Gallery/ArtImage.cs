using Zigote.UI.Net;

namespace AdwaitaGallery;

/// <summary>What an <see cref="ArtImage" /> is currently showing.</summary>
internal enum ArtState
{
    Loading,
    Ready,
    Failed,
}

/// <summary>
///     A picture from the network on an Adwaita card, with the three states a picture has and a
///     cross-fade between them: a spinner, then the artwork fading up under its artist credit, or a
///     quiet failure card if the bytes never arrive.
/// </summary>
/// <remarks>
///     <para>
///         <b>Lazy.</b> The load starts on mount, not on construction — inside a virtualized
///         <c>GridView.Builder</c> that means a tile fetches when it first scrolls into view, and a
///         thousand-tile feed only ever pays for the screenful the reader is looking at.
///     </para>
///     <para>
///         <b>Off the frame loop.</b> The fetch (or the disk read that replaces it) queues behind
///         <see cref="NetworkCache" />'s gate and the decode behind <c>Image</c>'s; the only work
///         that lands on the UI thread is the texture-handle swap and the animation it kicks off.
///     </para>
///     <para>
///         The decoded texture lives as long as this widget, not as long as the cell that shows it,
///         which is why the page keeps these in its model: a tile scrolled out and back in is a
///         re-attach, not a re-download.
///     </para>
/// </remarks>
internal sealed class ArtImage : ComposedWidget
{
    private readonly bool _chrome;
    private readonly AnimatedOpacity _fade;
    private readonly Image _image;
    private readonly uint _maxDim;
    private readonly AnimatedSwitcher _overlay;
    private readonly ArtPiece _piece;

    /// <param name="chrome">
    ///     The card and the credit chip. Off for a picture that is already framed by something else
    ///     — inside an <see cref="InteractiveViewer" />, where a zooming border reads as a bug.
    /// </param>
    public ArtImage(ArtPiece piece, uint maxDim, bool chrome = true)
    {
        _piece = piece;
        _maxDim = maxDim;
        _chrome = chrome;
        WasCached = NetworkCache.IsCached(piece.Url);

        _image = new Image {
            AltText = $"Anime artwork by {piece.Artist}",
            OnLoaded = () => Settle(ArtState.Ready, null),
            OnFailed = error => Settle(ArtState.Failed, Describe(error)),
        };
        _fade = new AnimatedOpacity(0f, Picture(), 0.4f);
        _overlay = new AnimatedSwitcher(Busy(), 0.25f);
    }

    /// <summary>Set to make the picture a button — the gallery opens the zoomable viewer with it.</summary>
    public Action? OnPressed { get; set; }

    /// <summary>Whether the bytes were already on disk when this was built — the cache readout.</summary>
    public bool WasCached { get; }

    /// <summary>Signal-backed so a page can react — enable a retry, count what failed.</summary>
    public Signal<ArtState> State { get; } = new(ArtState.Loading);

    /// <summary>Start over after a failure. A load already in flight is left alone.</summary>
    public void Reload()
    {
        if (State.Peek() == ArtState.Loading) return;
        State.Value = ArtState.Loading;
        _fade.Opacity = 0f;
        _overlay.Child = Busy();
        Load();
    }

    protected override void OnMount()
    {
        // The lazy half: first attach starts the fetch, a re-attach with a texture already in hand
        // does nothing. A load still in flight when the tile scrolled away is left running — it is
        // cheaper to finish it than to cancel and pay for the round trip again on the way back.
        if (!_image.HasTexture) Load();
    }

    protected override Widget Build(BuildContext context)
    {
        var layers = new Stack { Children = { _fade, _overlay } };
        if (!_chrome) return layers;

        var palette = AdwPalette.For(ThemeProvider.Of(context));
        Widget card = new DecoratedBox {
            Fill = palette.CardBg,
            Radius = AdwMetrics.CardRadius,
            BorderColor = palette.CardShade,
            BorderWidth = 1f,
            // The clip is what makes a letterboxed portrait sit inside the card's corners instead
            // of squaring them off.
            Child = new ClipRRect(AdwMetrics.CardRadius, layers),
        };

        if (OnPressed is { } pressed)
            card = new Pressable {
                OnPressed = pressed,
                FocusRadius = AdwMetrics.CardRadius,
                SemanticsLabel = $"Open artwork by {_piece.Artist}",
                Child = card,
            };

        return card;
    }

    private void Load()
    {
        // Fire-and-forget by design: LoadAsync never faults, and both outcomes come back through
        // OnLoaded/OnFailed on the UI thread.
        _image.LoadAsync(ct => NetworkCache.FetchAsync(_piece.Url, ct), _maxDim);
    }

    private void Settle(ArtState state, string? reason)
    {
        State.Value = state;
        _fade.Opacity = state == ArtState.Ready ? 1f : 0f;
        _overlay.Child = state == ArtState.Ready
            ? SizedBox.Shrink()
            : new BrokenArt(reason ?? "Couldn't load");
    }

    /// <summary>A line that fits under an icon and still says which layer gave up.</summary>
    private static string Describe(Exception error)
    {
        return error switch {
            HttpRequestException { StatusCode: { } status } => $"Server said {(int)status}",
            HttpRequestException => "No network",
            TaskCanceledException => "Timed out",
            InvalidDataException => "Not an image",
            _ => error.GetType().Name,
        };
    }

    private Widget Picture()
    {
        var picture = new Stack { Children = { new Center { Child = _image } } };
        if (_chrome)
            picture.Children.Add(
                new Align(
                    Alignment.BottomCenter,
                    new Padding(EdgeInsets.All(Spacing.Sm), new ArtCredit(_piece.Artist))
                )
            );
        return picture;
    }

    private static Widget Busy()
    {
        return new Center { Child = new AdwSpinner() };
    }
}

/// <summary>The artist credit riding on the bottom of a loaded picture.</summary>
internal sealed class ArtCredit(string artist) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        return new DecoratedBox {
            // Its own contrast rather than the theme's: the chip has to read on a bright sky and on
            // a black coat, and it does not know which it landed on.
            Fill = Color.Rgba(0, 0, 0, 0.55f),
            Radius = AdwMetrics.Pill,
            Child = new Padding(
                EdgeInsets.Symmetric(Spacing.Sm, Spacing.Xxs),
                new Label($"Art by {artist}", AdwTypography.Caption, Color.White) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            ),
        };
    }
}

/// <summary>The failure state — sized to fit a full carousel page and a thumbnail alike.</summary>
internal sealed class BrokenArt(string reason) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new Center {
            Child = new Column(
                spacing: Spacing.Sm,
                mainAxisSize: MainAxisSize.Min,
                crossAxisAlignment: CrossAxisAlignment.Center
            ) {
                Children = {
                    new IconGlyph(MaterialIcons.CloudOff, 28f, theme.Label3),
                    new Label(reason, AdwTypography.Caption, theme.TextSecondary) {
                        Align = TextAlign.Center,
                        MaxLines = 1,
                        Overflow = TextOverflow.Ellipsis,
                    },
                },
            },
        };
    }
}
