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
        var full = new ArtImage(piece: piece, maxDim: FullMaxDim, chrome: false);
        bool hero = from is { Texture.HasTexture: true };

        // Hidden while the tile's picture is in flight, then swapped in under it: the flight lands
        // exactly where this one sits, so the hand-over is a change of resolution and nothing else.
        var reveal = new AnimatedOpacity(opacity: hero ? 0f : 1f, child: full, duration: 0.01f);
        var viewer = new InteractiveViewer(reveal) {
            MaxScale = 10f,
            DoubleTapScale = 3f,
            OnScaleChanged = scale => zoom.Value = scale,
        };
        var flight = hero ? new HeroFlight(from: from!, frame: viewer) : null;
        if (flight is not null)
        {
            flight.OnLanded = () => reveal.Opacity = 1f;
            full.OnSettled = flight.Ready;
        }

        // The dialog raises its own toasts: the window's host sits under this dialog, so a toast
        // sent there would land where nobody can see it.
        AdwToastOverlay? toasts = null;
        var bar = new ViewerBar(piece: piece, toast: toast => toasts?.AddToast(Brief(toast)), viewer: viewer, zoom: zoom);

        Demo.ShowDialog(
            title: $"Art by {piece.Artist}",
            content: toasts = new AdwToastOverlay(
                new Stack {
                    Children = {
                        viewer,
                        new Align(
                            alignment: Alignment.BottomCenter,
                            child: new Padding(padding: EdgeInsets.Only(bottom: Spacing.Lg), child: bar)
                        ),
                    },
                }
            ),
            width: 920f,
            height: 720f,
            onClosing: () =>
            {
                if (flight is null) return;
                // Back from where the whole picture sits, whatever it was zoomed or panned to.
                viewer.Reset(animate: false);
                reveal.Opacity = 0f;
                flight.Return();
            }
        );
        flight?.Fly();
    }
}

/// <summary>
///     The shared-element transition between a grid tile and the viewer: an overlay that draws the
///     tile's own texture while it travels from the tile's frame to where the whole picture sits
///     in the viewer, un-cropping and squaring its corners on the way — and the reverse on close.
/// </summary>
internal sealed class HeroFlight : Widget
{
    private readonly AnimationController _anim;
    private readonly Widget _frame;
    private readonly ArtImage _from;
    private bool _arrived;
    private App? _host;
    private bool _ready;
    private Size _size;

    public HeroFlight(ArtImage from, Widget frame)
    {
        _from = from;
        _frame = frame;
        _anim = new AnimationController(durationSeconds: 0.35f, vsync: this) { Spec = AdwMotion.Reveal };
        _anim.OnTick += MarkNeedsPaint;
        _anim.OnCompleted += () =>
        {
            _arrived = true;
            Land();
        };
        _anim.OnDismissed += Leave;
    }

    /// <summary>Called when the flight has landed and the viewer's own picture is there to take over.</summary>
    public Action? OnLanded { get; set; }

    protected override void OnMount() => _anim.AttachTicker(this);

    /// <summary>Tile → viewer.</summary>
    public void Fly()
    {
        Enter();
        _anim.Forward();
    }

    /// <summary>Viewer → tile, from wherever the flight is: landed, or still on its way out.</summary>
    public void Return()
    {
        if (_host is null)
        {
            Enter();
            _anim.Seek(1f);
        }

        _arrived = false;
        _anim.Reverse();
    }

    /// <summary>The viewer's full-resolution picture has settled. Until it has, the flight holds its place.</summary>
    public void Ready()
    {
        _ready = true;
        Land();
    }

    private void Land()
    {
        if (!_arrived || !_ready || _host is null) return;
        OnLanded?.Invoke();
        Leave();
    }

    private void Enter()
    {
        if (_host is not null) return;
        _host = App.Active;
        _host?.PushOverlay(this);
    }

    private void Leave()
    {
        _host?.PopOverlay(this);
        _host = null;
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        return _size;
    }

    public override void Layout(Offset origin) =>
        Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);

    public override void Paint(PaintList paint)
    {
        var picture = _from.Texture;
        var (w, h) = picture.TextureSize;
        var tile = _from.Bounds;
        var frame = _frame.Bounds;
        if (!picture.HasTexture || h == 0 || tile.Width <= 0f || frame.Width <= 0f) return;

        // In the tile the picture covers its frame and the frame crops it; in the viewer the whole
        // picture fits. Moving the crop and the picture separately between those is the un-crop.
        float aspect = (float)w / h;
        var whole = Fit(frame: frame, aspect: aspect, cover: false);
        float t = _anim.Value;
        paint.AddClipStart(bounds: Lerp(a: tile, b: whole, t: t), radius: ArtImage.Radius * (1f - t));
        paint.AddImage(
            bounds: Lerp(a: Fit(frame: tile, aspect: aspect, cover: true), b: whole, t: t),
            pixelWidth: (int)w,
            pixelHeight: (int)h,
            pixels: null,
            cacheKey: picture.TextureHandle
        );
        paint.AddClipEnd();
    }

    // Never in the way: the dialog underneath keeps every click, the close button included.
    public override Widget? HitTest(Offset point) => null;

    private static Rect Fit(Rect frame, float aspect, bool cover)
    {
        bool byWidth = frame.Width / frame.Height < aspect != cover;
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

/// <summary>
///     One glass capsule floating over the picture, which runs edge to edge beneath it: the zoom
///     readout and the three things there are to do, and no strip of instructions — drag, pinch
///     and double-click are what anyone tries first. The capsule answers the pointer where it
///     lies, so it is kept small and low, off the part of the picture people reach for.
/// </summary>
internal sealed class ViewerBar(ArtPiece piece, Action<AdwToast> toast, InteractiveViewer viewer, Signal<float> zoom)
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
                padding: EdgeInsets.Only(left: Spacing.Lg, top: Spacing.Xs, right: Spacing.Xs, bottom: Spacing.Xs),
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
                        )),
                        new SizedBox(width: Spacing.Sm),
                        Demo.IconButton(icon: MaterialIcons.FitScreen, onPressed: () => viewer.Reset()),
                        Demo.IconButton(
                            icon: MaterialIcons.ContentCopy,
                            onPressed: () => ArtActions.Copy(piece: piece, toast: toast)
                        ),
                        new Watch(() => _saving.Value is >= 0f and var done
                            ? ArtActions.Percent(done: done, theme: theme)
                            : Demo.IconButton(
                                icon: MaterialIcons.Download,
                                onPressed: () => ArtActions.Save(piece: piece, toast: toast, saving: _saving)
                            )),
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
                    bool copied = result.TryGet(out byte[]? bytes, out _) &&
                                  ZigoteEngine.Instance?.SetClipboardData(mimeType: Mime(bytes), data: bytes) == true;
                    toast?.Invoke(new AdwToast(copied ? "Image copied" : "Couldn't copy the image"));
                }
            );
        }
    }

    /// <summary>
    ///     Saves the original to Downloads, driving <paramref name="saving" /> from 0 to 1 on the
    ///     way and back to negative when it settles. A second press while one is running is ignored.
    /// </summary>
    public static void Save(ArtPiece piece, Action<AdwToast>? toast, Signal<float> saving, Action? done = null)
    {
        if (saving.Peek() >= 0f) return;
        saving.Value = 0f;
        var app = App.Active;
        _ = Run();

        async Task Run()
        {
            // Reports arrive on the download's thread, in order; posting each keeps that order on
            // the UI thread, so the last one is always the settle below.
            var progress = new Posted(p => app?.Post(() => saving.Value = MathF.Max(x: 0f, y: (float)p.Fraction)));
            var (path, problem) = await ArtSource.DownloadAsync(piece: piece, progress: progress)
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
        child: new Row(mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Center) {
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
    public static Pressable CopyButton(ArtPiece piece, Action<AdwToast>? toast, ThemeData theme) => Glass(
        content: Icon(glyph: MaterialIcons.ContentCopy, theme: theme),
        onPressed: () => Copy(piece: piece, toast: toast),
        label: "Copy image"
    );

    /// <inheritdoc cref="CopyButton" />
    public static Pressable SaveButton(
        ArtPiece piece, Action<AdwToast>? toast, ThemeData theme, Signal<float> saving, Action? done = null) => Glass(
        content: new Watch(() => saving.Value is >= 0f and var at
            ? Percent(done: at, theme: theme)
            : Icon(glyph: MaterialIcons.Download, theme: theme)),
        onPressed: () => Save(piece: piece, toast: toast, saving: saving, done: done),
        label: "Save to Downloads"
    );

    private static Widget Icon(string glyph, ThemeData theme) => new SizedBox(
        width: 34f,
        height: 34f,
        child: new Center { Child = new IconGlyph(glyph: glyph, size: 18f, color: LiquidPane.OnGlass(theme)) }
    );

    private static Pressable Glass(Widget content, Action onPressed, string label) =>
        LiquidPane.Interactive(
            pane: new LiquidPane { Elevation = 4f, Child = content },
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
