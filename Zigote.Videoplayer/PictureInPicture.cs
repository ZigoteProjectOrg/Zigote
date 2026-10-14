using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.Core.State;
using Zigote.UI.Adwaita;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Videoplayer;

/// <summary>
///     Picture-in-picture: the video keeps playing in a small floating player while the app — or
///     another app — has the screen.
///     <para>
///         On a desktop it is its own OS window: borderless with rounded corners, above every other
///         application's windows, locked to the video's aspect while resized, dragged from anywhere
///         on the picture. Where the platform has no secondary windows (phones, a layer-shell
///         host) it floats inside the app window instead, dragged and snapped to a corner.
///     </para>
///     <para>
///         One at a time, as on every OS: opening a second closes the first. It shares the
///         player with whatever view opened it — nothing is re-opened or re-buffered.
///     </para>
/// </summary>
public static class PictureInPicture
{
    private const float Width = 400f;

    private static readonly Signal<VideoPlayer?> CurrentSignal = new(null);
    private static App? _window;
    private static (App Host, Widget Overlay)? _floating;

    /// <summary>The player in picture-in-picture right now, or null. Views bind to it.</summary>
    public static IReadableSignal<VideoPlayer?> Current => CurrentSignal;

    public static void Open(VideoPlayer player, App? from = null)
    {
        Close();
        var host = from ?? App.Active;
        if (host is null) return;
        var main = host.ParentApp ?? host;

        float aspect = Aspect(player);
        uint w = (uint)Width;
        uint h = (uint)MathF.Round(Width / aspect);
        CurrentSignal.Value = player;

        App window;
        try
        {
            window = main.CreateWindow(title: "Picture in Picture", width: w, height: h);
        }
        catch (InvalidOperationException)
        {
            var overlay = new FloatingPip(new PipView(player));
            main.PushOverlay(overlay);
            _floating = (main, overlay);
            return;
        }

        // Borderless with app-drawn (no) chrome: the picture is the whole window, CsdDragSurfaces
        // make it the titlebar, and the native hit-test supplies resize edges.
        window.SuppressChromeStrip = true;
        window.ApplyWindowChrome(WindowChromeStyle.AdwaitaCsd);
        window.Engine.WindowSetAlwaysOnTop(windowId: window.WindowId, on: true);
        window.Engine.WindowSetAspectRatio(windowId: window.WindowId, aspect: aspect);
        window.Theme = main.Theme;
        window.Root = new AdwDragArea(new PipView(player));
        window.CloseRequested += () =>
        {
            _window = null; // closing itself; Close() must not close it again
            Close();
        };

        // Bottom-right of the window it came from, the corner a player conventionally parks in.
        (int mx, int my) = main.Engine.MainWindowPosition();
        window.NativeWindow!.SetPosition(
            x: mx + (int)main.Engine.LogicalWidth - (int)w - 24,
            y: my + (int)main.Engine.LogicalHeight - (int)h - 24
        );
        _window = window;
    }

    /// <summary>Close picture-in-picture, if open. Playback carries on in the view that opened it.</summary>
    public static void Close()
    {
        if (_window is { } window)
        {
            _window = null;
            window.Close();
        }

        if (_floating is { } floating)
        {
            _floating = null;
            floating.Host.PopOverlay(floating.Overlay);
        }

        CurrentSignal.Value = null;
    }

    internal static float Aspect(VideoPlayer player)
    {
        (uint fw, uint fh) = player.FrameSize;
        if (fw > 0 && fh > 0) return (float)fw / fh;
        return (float)(player.Media.Value?.AspectRatio ?? 16.0 / 9.0);
    }
}

/// <summary>
///     The mini player: the picture, and on hover a close button, a "back to the app" button,
///     ±10 s and play/pause in the middle, and a thin scrubber along the bottom edge.
/// </summary>
internal sealed class PipView(VideoPlayer player) : VideoChrome(player)
{
    private AdwButton _play = null!;
    private Widget? _root;
    private MediaSlider _seek = null!;

    protected override void OnMount()
    {
        base.OnMount();
        Own(Player.Position.Observe(() => OnUi(SyncTime)));
        Own(Player.State.Observe(() => OnUi(SyncTransport)));
        AutoHideDelay = TimeSpan.FromSeconds(2);
    }

    protected override bool HoldControls() => base.HoldControls() || _seek.Dragging;

    protected override Widget Build(BuildContext context)
    {
        if (_root is not null) return _root;

        _play = IconButton(
            icon: MaterialIcons.Pause,
            label: "Pause",
            onPressed: Player.TogglePlayPause
        );
        _seek = new MediaSlider {
            OnChangeEnd = f => Player.Seek(Player.Duration * f),
            SemanticsLabel = "Seek",
        };

        Controls.Child = new Stack {
            Children = {
                Positioned.Fill(new PipScrim()),
                new Positioned(
                    left: 8,
                    top: 8,
                    child: Pane(
                        radius: 17f,
                        child: IconButton(
                            icon: MaterialIcons.OpenInNew,
                            label: "Back to app",
                            onPressed: PictureInPicture.Close
                        )
                    )
                ),
                new Positioned(
                    right: 8,
                    top: 8,
                    child: Pane(
                        radius: 17f,
                        child: IconButton(
                            icon: MaterialIcons.Close,
                            label: "Close",
                            onPressed: CloseAndPause
                        )
                    )
                ),
                Positioned.Fill(
                    new Center(
                        Pane(
                            radius: 24f,
                            child: new Padding(
                                padding: EdgeInsets.All(7f),
                                child: new Row(mainAxisSize: MainAxisSize.Min, spacing: 8f) {
                                    Children = {
                                        IconButton(
                                            icon: MaterialIcons.Replay10,
                                            label: "Back 10 seconds",
                                            onPressed:
                                            () => Player.SeekBy(TimeSpan.FromSeconds(-10))
                                        ),
                                        _play,
                                        IconButton(
                                            icon: MaterialIcons.Forward10,
                                            label: "Forward 10 seconds",
                                            onPressed: () => Player.SeekBy(TimeSpan.FromSeconds(10))
                                        ),
                                    },
                                }
                            )
                        )
                    )
                ),
                new Positioned(
                    left: 10,
                    right: 10,
                    bottom: 4,
                    child: _seek
                ),
            },
        };

        _root = new ThemeProvider(
            data: OsdTheme(ThemeProvider.Of(context)),
            child: new Stack {
                Children = {
                    new VideoView(Player),
                    Positioned.Fill(new PipSurface(this)),
                    Positioned.Fill(Controls),
                },
            }
        );
        Built = true;
        SyncTime();
        SyncTransport();
        return _root;
    }

    /// <summary>Closing the mini player stops the video too — it is what ✕ on a floating player means.</summary>
    private void CloseAndPause()
    {
        Player.Pause();
        PictureInPicture.Close();
    }

    private void SyncTime()
    {
        if (!_seek.Dragging) _seek.Value = (float)Player.Progress;
        _seek.Enabled = Player.Media.Value?.IsSeekable ?? false;
    }

    private void SyncTransport()
    {
        bool playing = Player.IsPlaying;
        SetIcon(button: _play, icon: playing ? MaterialIcons.Pause : MaterialIcons.PlayArrow);
        _play.Label = playing ? "Pause" : "Play";
    }
}

/// <summary>
///     Hover-only on purpose: it overrides no press handler, so in the OS window the drag
///     arbiter treats the picture as titlebar and the whole video moves the window.
/// </summary>
internal sealed class PipSurface(PipView view) : Widget
{
    private Size _size;

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        return _size;
    }

    public override void Layout(Offset origin) => Bounds = new Rect(
        x: origin.X,
        y: origin.Y,
        width: _size.Width,
        height: _size.Height
    );

    public override void Paint(PaintList paint) { }

    public override void OnPointerEnter() => view.ShowControls();

    public override void OnPointerMove(Offset point) => view.ShowControls();
}

/// <summary>The dim behind the mini player's controls, so white glyphs read over a white frame.</summary>
internal sealed class PipScrim : Widget
{
    private Size _size;

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        return _size;
    }

    public override void Layout(Offset origin) => Bounds = new Rect(
        x: origin.X,
        y: origin.Y,
        width: _size.Width,
        height: _size.Height
    );

    public override void Paint(PaintList paint) => paint.AddRect(
        bounds: Bounds,
        color: Colors.Black.WithAlpha(0.2f)
    );

    // Paint only; the picture under it keeps the pointer.
    public override Widget? HitTest(Offset point) => null;
}

/// <summary>
///     The in-window fallback: a card floating over the app, dragged by the picture and snapped to
///     the nearest corner on release.
/// </summary>
internal sealed class FloatingPip(PipView view) : Widget
{
    private const float Margin = 16f;
    private const float CardWidth = 240f;

    private bool _dragging;
    private Offset _grab;
    private Rect _card;
    private bool _placed;
    private Size _size;

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        float w = MathF.Min(x: CardWidth, y: _size.Width - (Margin * 2f));
        float h = w / PictureInPicture.Aspect(view.Player);
        view.Measure(Constraints.Tight(width: w, height: h));
        if (!_placed)
            _card = new Rect(
                x: _size.Width - w - Margin,
                y: _size.Height - h - Margin,
                width: w,
                height: h
            );
        else
            _card = new Rect(
                x: _card.X,
                y: _card.Y,
                width: w,
                height: h
            );
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
        view.Layout(new Offset(x: _card.X, y: _card.Y));
    }

    public override void Paint(PaintList paint)
    {
        paint.AddElevation(bounds: _card, radius: 12f, style: Elevation.Z2);
        paint.AddClipStart(bounds: _card, radius: 12f);
        view.Paint(paint);
        paint.AddClipEnd();
    }

    // Only the card is here; the rest of the window belongs to the app underneath.
    public override Widget? HitTest(Offset point)
    {
        if (!_card.Contains(px: point.X, py: point.Y)) return null;
        var hit = view.HitTest(point);
        return hit is PipSurface ? this : hit;
    }

    public override void OnPointerMove(Offset point)
    {
        view.ShowControls();
        if (!_dragging) return;
        _placed = true;
        _card = new Rect(
            x: point.X - _grab.X,
            y: point.Y - _grab.Y,
            width: _card.Width,
            height: _card.Height
        );
        MarkNeedsLayout();
    }

    public override void OnPointerDown(Offset point)
    {
        _dragging = true;
        _grab = new Offset(x: point.X - _card.X, y: point.Y - _card.Y);
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_dragging) return;
        _dragging = false;
        // Snap to whichever corner the card's centre is nearest.
        bool right = _card.X + (_card.Width / 2f) > _size.Width / 2f;
        bool bottom = _card.Y + (_card.Height / 2f) > _size.Height / 2f;
        _card = new Rect(
            x: right ? _size.Width - _card.Width - Margin : Margin,
            y: bottom ? _size.Height - _card.Height - Margin : Margin,
            width: _card.Width,
            height: _card.Height
        );
        MarkNeedsLayout();
    }

    public override bool CanTouchDrag(bool vertical) => _dragging;

    public override IEnumerable<Widget> GetChildren() => [view];
}
