using Zigote.Core.Animation;
using Zigote.Core.Engine;
using Zigote.Http;

namespace AdwaitaGallery;

/// <summary>
///     The full-size picture in an <see cref="InteractiveViewer" />: drag to pan, pinch or
///     ⌘/Ctrl-scroll to zoom about the pointer, double-click to toggle between fit and 3×. A glass
///     toolbar floats over it with the zoom, fit, copy and save.
/// </summary>
/// <remarks>
///     Opened from a grid tile or the carousel's expand button, and reloaded at four times the
///     thumbnail's resolution — zoom can only show what was decoded, so the deep zoom needs a decode
///     that deserves it. The bytes are the same ones the thumbnail already put on disk, so the
///     re-decode costs a disk read rather than a round trip.
/// </remarks>
internal static class ArtViewer
{
    // 2048 on the long edge is 16 MB of GPU for one picture, which is fine for the one dialog at a
    // time this opens, and holds up to roughly 8× zoom before the pixels show.
    private const uint FullMaxDim = 2048;

    // The toast rises over the toolbar, so it does not get the usual five seconds to sit there.
    private static AdwToast Brief(AdwToast toast)
    {
        toast.Timeout = 3f;
        return toast;
    }

    /// <param name="from">
    ///     The tile this was opened from. Its picture flies out of the grid into the viewer and
    ///     back again on close, so the thing pressed is visibly the thing shown.
    /// </param>
    public static void Show(ArtPiece piece, ArtImage? from = null)
    {
        var zoom = new Signal<float>(1f);
        bool hero = from is { Texture.HasTexture: true };
        // No spinner over a picture that is already there: the tile's own texture stands in.
        var full = new ArtImage(
            piece: piece,
            maxDim: FullMaxDim,
            chrome: false,
            spinner: !hero
        );

        // Hidden while the tile's picture is in flight, and shown the frame it lands. Under the
        // full picture sits the tile's texture at the same fit, so the landing is seamless and the
        // sharper decode fades up over it whenever it arrives — nothing waits on the load.
        var landing = hero
            ? new AdwHero(
                tag: from!,
                child: new Stack {
                    Children = {
                        new Poster(from!.Texture),
                        full,
                    },
                }
            ) {
                Flight = ArtFlight.Uncrop(from!.Texture),
            }
            : null;
        var viewer = new InteractiveViewer(landing ?? (Widget)full) {
            MaxScale = 10f,
            DoubleTapScale = 3f,
            OnScaleChanged = scale => zoom.Value = scale,
        };

        // The dialog raises its own toasts: the window's host sits under this dialog, so a toast
        // sent there would land where nobody can see it.
        AdwToastOverlay? toasts = null;
        // The flight passes over where the toolbar sits, so the toolbar waits for it to land and
        // fades up then, rather than popping out from under it.
        var bar = new AnimatedOpacity(
            opacity: hero ? 0f : 1f,
            child: new ViewerBar(
                piece: piece,
                toast: toast => toasts?.AddToast(Brief(toast)),
                viewer: viewer,
                zoom: zoom
            ),
            duration: 0.2f
        );
        if (landing is not null) landing.OnLanded = () => bar.Opacity = 1f;

        Demo.ShowDialog(
            title: $"Art by {piece.Artist}",
            content: toasts = new AdwToastOverlay(
                new Stack {
                    Children = {
                        viewer,
                        new Align(
                            alignment: Alignment.BottomCenter,
                            child: new Padding(
                                padding: EdgeInsets.Only(bottom: Spacing.Lg),
                                child: bar
                            )
                        ),
                    },
                }
            ),
            width: 920f,
            height: 720f,
            onClosing: () =>
            {
                if (landing is null) return;
                // The flight home starts from where the whole picture sits, whatever it was zoomed
                // or panned to — the dialog sends the hero back right after this.
                viewer.Reset(animate: false);
                bar.Opacity = 0f;
            }
        );
    }
}

/// <summary>
///     The flight between a grid tile and the viewer: the tile's own texture, un-cropping and
///     squaring its corners on the way out, and the reverse on the way home.
/// </summary>
internal static class ArtFlight
{
    public static AdwHeroPainter Uncrop(Image picture) => (paint, from, to, t) =>
    {
        (uint w, uint h) = picture.TextureSize;
        if (!picture.HasTexture || h == 0) return;

        // In the tile the picture covers its frame and the frame crops it; in the viewer the whole
        // picture fits. Moving the crop and the picture separately between those is the un-crop.
        float aspect = (float)w / h;
        var whole = Fit(frame: to, aspect: aspect, cover: false);
        paint.AddClipStart(
            bounds: Lerp(a: from, b: whole, t: t),
            radius: ArtImage.Radius * (1f - t)
        );
        paint.AddImage(
            bounds: Lerp(a: Fit(frame: from, aspect: aspect, cover: true), b: whole, t: t),
            pixelWidth: (int)w,
            pixelHeight: (int)h,
            pixels: null,
            cacheKey: picture.TextureHandle
        );
        paint.AddClipEnd();
    };

    public static Rect Fit(Rect frame, float aspect, bool cover)
    {
        bool byWidth = (frame.Width / frame.Height < aspect) != cover;
        float width = byWidth ? frame.Width : frame.Height * aspect;
        float height = byWidth ? frame.Width / aspect : frame.Height;
        return new Rect(
            x: frame.X + ((frame.Width - width) / 2f),
            y: frame.Y + ((frame.Height - height) / 2f),
            width: width,
            height: height
        );
    }

    private static Rect Lerp(Rect a, Rect b, float t) => new(
        x: a.X + ((b.X - a.X) * t),
        y: a.Y + ((b.Y - a.Y) * t),
        width: a.Width + ((b.Width - a.Width) * t),
        height: a.Height + ((b.Height - a.Height) * t)
    );
}

/// <summary>The tile's texture, whole, fitted to the frame — what the flight lands on.</summary>
internal sealed class Poster(Image picture) : Widget
{
    private Size _size;

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        return _size;
    }

    public override void Layout(Offset origin) =>
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

    public override void Paint(PaintList paint)
    {
        (uint w, uint h) = picture.TextureSize;
        if (!picture.HasTexture || h == 0) return;
        paint.AddImage(
            bounds: ArtFlight.Fit(frame: Bounds, aspect: (float)w / h, cover: false),
            pixelWidth: (int)w,
            pixelHeight: (int)h,
            pixels: null,
            cacheKey: picture.TextureHandle
        );
    }

    public override Widget? HitTest(Offset point) => null;
}

/// <summary>
///     One glass capsule floating over the picture, which runs edge to edge beneath it: the zoom
///     readout and the three things there are to do, and no strip of instructions — drag, pinch
///     and double-click are what anyone tries first. The capsule answers the pointer where it
///     lies, so it is kept small and low, off the part of the picture people reach for.
/// </summary>
internal sealed class ViewerBar(
    ArtPiece piece,
    Action<AdwToast> toast,
    InteractiveViewer viewer,
    Signal<float> zoom)
    : ComposedWidget
{
    private readonly Signal<float> _saving = new(-1f);

    protected override Widget Build(BuildContext context)
    {
        // Read here and captured: a Watch builder is not a reliable place to look the theme up.
        var theme = ThemeProvider.Of(context);
        return LiquidPane.Regular(
            radius: AdwMetrics.Pill,
            elevation: 8f,
            child: new Padding(
                padding: EdgeInsets.Only(
                    left: Spacing.Lg,
                    top: Spacing.Xs,
                    right: Spacing.Xs,
                    bottom: Spacing.Xs
                ),
                child: new Row(
                    spacing: Spacing.Xs,
                    mainAxisSize: MainAxisSize.Min,
                    crossAxisAlignment: CrossAxisAlignment.Center
                ) {
                    Children = {
                        new Watch(() => new Label(
                                text: $"{zoom.Value:0.0}×",
                                style: AdwTypography.Monospace,
                                color: LiquidPane.OnGlass(theme)
                            )
                        ),
                        new SizedBox(width: Spacing.Sm),
                        Demo.IconButton(
                            icon: MaterialIcons.FitScreen,
                            onPressed: () => viewer.Reset()
                        ),
                        Demo.IconButton(
                            icon: MaterialIcons.ContentCopy,
                            onPressed: () => ArtActions.Copy(piece: piece, toast: toast)
                        ),
                        new Watch(() => _saving.Value is >= 0f and var done
                            ? ArtActions.Percent(done: done, theme: theme)
                            : Demo.IconButton(
                                icon: MaterialIcons.Download,
                                onPressed: () => ArtActions.Save(
                                    piece: piece,
                                    toast: toast,
                                    saving: _saving
                                )
                            )
                        ),
                    },
                }
            )
        );
    }
}

/// <summary>
///     Copy and save, for a tile's hover buttons and the viewer's toolbar alike. Both run off the
///     UI thread and come back to it only to say how it went.
/// </summary>
internal static class ArtActions
{
    /// <summary>
    ///     Puts the original picture — the encoded file, not a screenshot of the tile — on the
    ///     clipboard, so it pastes into a chat or an editor at full resolution.
    /// </summary>
    public static void Copy(ArtPiece piece, Action<AdwToast>? toast)
    {
        var app = App.Active;
        _ = Run();

        async Task Run()
        {
            var result = await ArtSource.BytesAsync(piece).ConfigureAwait(false);
            app?.Post(() =>
                {
                    bool copied = result.TryGet(value: out byte[]? bytes, error: out _) &&
                                  ZigoteEngine.Instance?.SetClipboardData(
                                      mimeType: Mime(bytes),
                                      data: bytes
                                  ) == true;
                    toast?.Invoke(
                        new AdwToast(copied ? "Image copied" : "Couldn't copy the image")
                    );
                }
            );
        }
    }

    /// <summary>
    ///     Saves the original to Downloads, driving <paramref name="saving" /> from 0 to 1 on the
    ///     way and back to negative when it settles. A second press while one is running is ignored.
    /// </summary>
    public static void Save(ArtPiece piece, Action<AdwToast>? toast, Signal<float> saving,
        Action? done = null)
    {
        if (saving.Peek() >= 0f) return;
        saving.Value = 0f;
        var app = App.Active;
        _ = Run();

        async Task Run()
        {
            // Reports arrive on the download's thread, in order; posting each keeps that order on
            // the UI thread, so the last one is always the settle below.
            var progress = new Posted(p =>
                app?.Post(() => saving.Value = MathF.Max(x: 0f, y: (float)p.Fraction))
            );
            (string? path, string? problem) = await ArtSource
                .DownloadAsync(piece: piece, progress: progress)
                .ConfigureAwait(false);
            app?.Post(() =>
                {
                    saving.Value = -1f;
                    done?.Invoke();
                    if (path is not null)
                    {
                        toast?.Invoke(
                            new AdwToast("Saved to Downloads") {
                                ButtonLabel = "Show",
                                OnButtonClicked = () => FileOperations.RevealInFileManager(path),
                            }
                        );
                    }
                    else if (problem is not null) toast?.Invoke(new AdwToast(problem));
                }
            );
        }
    }

    /// <summary>The save button's face while a download runs.</summary>
    public static Widget Percent(float done, ThemeData theme) => new SizedBox(
        height: 34f,
        child: new Row(
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        ) {
            Children = {
                new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: Spacing.Sm),
                    child: new Label(
                        text: $"{done * 100f:0}%",
                        style: AdwTypography.Monospace,
                        color: LiquidPane.OnGlass(theme)
                    )
                ),
            },
        }
    );

    /// <summary>A round glass button for chrome that rides directly on a picture.</summary>
    public static Pressable CopyButton(ArtPiece piece, Action<AdwToast>? toast, ThemeData theme) =>
        Glass(
            content: Icon(glyph: MaterialIcons.ContentCopy, theme: theme),
            onPressed: () => Copy(piece: piece, toast: toast),
            label: "Copy image"
        );

    /// <inheritdoc cref="CopyButton" />
    public static Pressable SaveButton(
        ArtPiece piece, Action<AdwToast>? toast, ThemeData theme, Signal<float> saving,
        Action? done = null) => Glass(
        content: new Watch(() => saving.Value is >= 0f and var at
            ? Percent(done: at, theme: theme)
            : Icon(glyph: MaterialIcons.Download, theme: theme)
        ),
        onPressed: () => Save(
            piece: piece,
            toast: toast,
            saving: saving,
            done: done
        ),
        label: "Save to Downloads"
    );

    private static Widget Icon(string glyph, ThemeData theme) => new SizedBox(
        width: 34f,
        height: 34f,
        child: new Center {
            Child = new IconGlyph(glyph: glyph, size: 18f, color: LiquidPane.OnGlass(theme)),
        }
    );

    private static Pressable Glass(Widget content, Action onPressed, string label) =>
        LiquidPane.Interactive(
            pane: new LiquidPane {
                Elevation = 4f,
                Child = content,
            },
            onPressed: onPressed,
            semantics: label
        );

    // What the bytes are, not what the URL's extension claims: the clipboard is told the truth.
    private static string Mime(ReadOnlySpan<byte> bytes) => bytes switch {
        [0xFF, 0xD8, ..] => "image/jpeg",
        [(byte)'G', (byte)'I', (byte)'F', ..] => "image/gif",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', ..] => "image/webp",
        _ => "image/png",
    };

    private sealed class Posted(Action<HttpProgress> report) : IProgress<HttpProgress>
    {
        public void Report(HttpProgress value) => report(value);
    }
}
