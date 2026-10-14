using Zigote.Http;

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
///         <b>Off the frame loop.</b> The fetch (or the disk-cache read that replaces it) queues
///         behind the shared runner's per-host gate and the decode behind <c>Image</c>'s; the only
///         work that lands on the UI thread is the texture-handle swap and the animation it kicks
///         off.
///     </para>
///     <para>
///         The decoded texture lives as long as this widget, not as long as the cell that shows it,
///         which is why the page keeps these in its model: a tile scrolled out and back in is a
///         re-attach, not a re-download.
///     </para>
/// </remarks>
internal sealed class ArtImage : ComposedWidget
{
    // Larger than the Adwaita card's 12: a picture with nothing drawn around it reads as an object,
    // and an object wants the rounder corner a photo tile has.
    private const float TileRadius = 18f;

    // Empty until the pointer is over a pressable tile: the two glass buttons are two more lenses,
    // and a screenful of tiles should pay for them on one tile at a time.
    private readonly AnimatedSwitcher _actions;
    private readonly bool _chrome;
    private readonly bool _cover;

    // 0–1 while this picture is being saved, negative otherwise.
    private readonly Signal<float> _saving = new(-1f);
    private Widget? _buttons;
    private Pressable? _card;
    private Pressable[] _hot = [];
    private readonly AnimatedOpacity _fade;
    private readonly Widget _idle = SizedBox.Shrink();
    private readonly Image _image;
    private readonly uint _maxDim;
    private readonly AnimatedSwitcher _overlay;
    private readonly ArtPiece _piece;

    /// <param name="chrome">
    ///     The card and the credit chip. Off for a picture that is already framed by something else
    ///     — inside an <see cref="InteractiveViewer" />, where a zooming border reads as a bug.
    /// </param>
    /// <param name="cover">
    ///     Fill the frame and crop the overflow, as a photo grid does, instead of letterboxing the
    ///     whole picture inside it.
    /// </param>
    /// <param name="spinner">Off when something else already stands in for the picture while it loads.</param>
    public ArtImage(ArtPiece piece, uint maxDim, bool chrome = true, bool cover = false,
        bool spinner = true)
    {
        _piece = piece;
        _maxDim = maxDim;
        _chrome = chrome;
        _cover = cover;
        _actions = new AnimatedSwitcher(child: _idle, duration: 0.18f);

        _image = new Image {
            AltText = $"Anime artwork by {piece.Artist}",
            OnLoaded = () => Settle(state: ArtState.Ready, reason: null),
            OnFailed = error => Settle(state: ArtState.Failed, reason: Describe(error)),
        };
        // Tagged with this picture itself: whatever opens it flies it out of here by that tag.
        _fade = new AnimatedOpacity(
            opacity: 0f,
            child: new AdwHero(tag: this, child: Picture()),
            duration: 0.4f
        );
        _overlay = new AnimatedSwitcher(
            child: spinner ? Busy() : SizedBox.Shrink(),
            duration: 0.25f
        );
    }

    /// <summary>The corner a framed picture is clipped to.</summary>
    public const float Radius = TileRadius;

    /// <summary>The decoded image widget — what a transition draws while it carries it elsewhere.</summary>
    public Image Texture => _image;


    /// <summary>Set to make the picture a button — the gallery opens the zoomable viewer with it.</summary>
    public Action? OnPressed { get; set; }

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
        var layers = new Stack {
            Children = {
                _fade,
                _overlay,
            },
        };
        if (!_chrome) return layers;

        // No border and no card colour around a loaded picture: the artwork is the tile. The fill
        // only shows while it loads, as the placeholder the picture fades up over.
        var theme = ThemeProvider.Of(context);
        Widget card = new DecoratedBox {
            Fill = AdwPalette.For(theme).CardBg,
            Radius = TileRadius,
            // The clip is what makes the picture take the tile's corners instead of squaring them off.
            Child = new ClipRRect(radius: TileRadius, child: layers),
        };

        if (OnPressed is not { } pressed) return card;

        _card = new Pressable {
            OnPressed = pressed,
            FocusRadius = TileRadius,
            SemanticsLabel = $"Open artwork by {_piece.Artist}",
            Child = card,
            OnStateChanged = QueueHover,
        };

        // No shell above a page shown on its own in the preview: the buttons still work there,
        // they just have nowhere to say so.
        var host = GalleryHost.Find(context);
        Action<AdwToast> toast = raised => host?.Toast(
            title: raised.Title,
            buttonLabel: raised.ButtonLabel,
            onButtonClicked: raised.OnButtonClicked
        );
        var copy = ArtActions.CopyButton(piece: _piece, toast: toast, theme: theme);
        var save = ArtActions.SaveButton(
            piece: _piece,
            toast: toast,
            theme: theme,
            saving: _saving,
            done: QueueHover
        );
        _hot = [_card, copy, save];
        foreach (var button in _hot.AsSpan(1))
        {
            // Interactive() already listens here to drive the pane's gel — keep that and add ours.
            var gel = button.OnStateChanged;
            button.OnStateChanged = () =>
            {
                gel?.Invoke();
                QueueHover();
            };
        }

        _buttons = new Row(spacing: Spacing.Xs, mainAxisSize: MainAxisSize.Min) {
            Children = {
                copy,
                save,
            },
        };
        return new Stack {
            Children = {
                _card,
                new Align(
                    alignment: Alignment.TopRight,
                    child: new Padding(padding: EdgeInsets.All(Spacing.Sm), child: _actions)
                ),
            },
        };
    }

    // Posted, not run in place: the pointer crossing from the picture onto a button is an exit and
    // then an enter, and deciding between the two would drop the buttons out from under the pointer.
    private void QueueHover() => App.Active?.Post(() =>
        {
            bool show = State.Peek() == ArtState.Ready &&
                        (_saving.Peek() >= 0f || Array.Exists(
                            array: _hot,
                            match: static p => p.Hovered
                        ));
            var next = show && _buttons is not null ? _buttons : _idle;
            if (!ReferenceEquals(objA: _actions.Child, objB: next)) _actions.Child = next;
        }
    );

    private void Load()
    {
        // Fire-and-forget by design: LoadAsync never faults, and both outcomes come back through
        // OnLoaded/OnFailed on the UI thread. Unwrap() is the sanctioned bridge from the runner's
        // result values to the exception LoadAsync's fetch contract wants — Describe below takes
        // the HttpError back out.
        _image.LoadAsync(
            fetch: async ct =>
                (await ArtSource.Http.BytesAsync(spec: HttpRequest.Get(_piece.Url), ct: ct)
                    .ConfigureAwait(false))
                .Unwrap(),
            maxDim: _maxDim
        );
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
            HttpException { Error: HttpError.Status status } => $"Server said {(int)status.Code}",
            HttpException { Error: HttpError.Timeout } => "Timed out",
            HttpException { Error: HttpError.Transport } => "No network",
            HttpException http => http.Error.Message,
            InvalidDataException => "Not an image",
            _ => error.GetType().Name,
        };
    }

    private Widget Picture()
    {
        var picture = new Stack {
            // Unframed means the viewer, where the picture fills what it is given — scaled up too,
            // so a small original lands where its thumbnail did instead of shrinking on arrival.
            Children = {
                _cover || !_chrome
                    ? new Cover(image: _image, cover: _cover)
                    : new Center { Child = _image },
            },
        };
        if (_chrome)
        {
            picture.Children.Add(
                new Align(
                    alignment: Alignment.BottomLeft,
                    child: new Padding(
                        padding: EdgeInsets.All(Spacing.Sm),
                        child: new ArtCredit(_piece.Artist)
                    )
                )
            );
        }

        return picture;
    }

    private static Widget Busy() => new Center { Child = new AdwSpinner() };
}

/// <summary>
///     Aspect-fill: the picture scaled until it covers the frame, centred, the overflow left for the
///     clip above to cut — or, with <paramref name="cover" /> off, scaled until it just fits, up as
///     well as down. <see cref="Image" /> on its own only ever shrinks to fit.
/// </summary>
internal sealed class Cover(Image image, bool cover = true) : Widget
{
    private Size _picture;
    private Size _size;

    public override Size Measure(Constraints c)
    {
        (uint w, uint h) = image.TextureSize;
        if (w == 0 || h == 0 || !float.IsFinite(c.MaxWidth) || !float.IsFinite(c.MaxHeight))
        {
            // Nothing decoded yet, or no frame to cover: behave like the image itself.
            _size = _picture = image.Measure(c);
            return _size;
        }

        _size = new Size(width: c.MaxWidth, height: c.MaxHeight);
        float sx = _size.Width / w, sy = _size.Height / h;
        float scale = cover ? MathF.Max(x: sx, y: sy) : MathF.Min(x: sx, y: sy);
        _picture = image.Measure(Constraints.Tight(width: w * scale, height: h * scale));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        image.Layout(
            new Offset(
                x: origin.X + ((_size.Width - _picture.Width) / 2f),
                y: origin.Y + ((_size.Height - _picture.Height) / 2f)
            )
        );
    }

    public override void Paint(PaintList paint) => image.Paint(paint);

    public override Widget? HitTest(Offset point) => null;

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(image);
}

/// <summary>The artist credit riding on the bottom of a loaded picture.</summary>
internal sealed class ArtCredit(string artist) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        // Liquid Glass rather than a flat chip, because this rides on a picture it cannot see: the
        // pane's adaptive scrim answers the bright-sky/black-coat problem per pixel, the theme
        // picks the glass family (dark glass + white here, light glass + ink there), and the lens
        // refracts the artwork through itself, so the chip belongs to the picture instead of
        // covering it.
        var theme = ThemeProvider.Of(context);
        return new LiquidPane {
            Child = new Padding(
                padding: EdgeInsets.Symmetric(horizontal: Spacing.Sm, vertical: Spacing.Xxs),
                child: new Label(
                    text: $"Art by {artist}",
                    style: AdwTypography.Caption,
                    color: LiquidPane.OnGlass(theme)
                ) {
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
                    new IconGlyph(glyph: MaterialIcons.CloudOff, size: 28f, color: theme.Label3),
                    new Label(
                        text: reason,
                        style: AdwTypography.Caption,
                        color: theme.TextSecondary
                    ) {
                        Align = TextAlign.Center,
                        MaxLines = 1,
                        Overflow = TextOverflow.Ellipsis,
                    },
                },
            },
        };
    }
}
