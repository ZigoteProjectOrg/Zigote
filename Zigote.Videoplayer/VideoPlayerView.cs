using System.Diagnostics;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.State;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.Videoplayer;

/// <summary>
///     A complete video player: the picture with its controls floating over it, the way a
///     messenger's media viewer does it.
///     <list type="bullet">
///         <item>
///             The controls are a dark panel over the bottom of the frame that fades out three
///             seconds into playback and back in on any pointer movement; paused, they stay.
///         </item>
///         <item>
///             Click to play or pause, double-click for fullscreen. Press and hold to play at 2×
///             for as long as the button is down. On touch a tap shows the controls instead, and
///             a double-tap on either half seeks ten seconds that way.
///         </item>
///         <item>
///             Space or K plays, ← → seek 5 s, J L seek 10 s, ↑ ↓ change the volume, M mutes,
///             0–9 jump to that tenth, F toggles fullscreen, P picture-in-picture; Esc or back leaves fullscreen.
///         </item>
///         <item>Hovering the scrubber shows the time under the pointer before a click commits to it.</item>
///     </list>
///     It does not own the player; disposing the view leaves the player to whoever created it.
/// </summary>
public sealed class VideoPlayerView : VideoChrome, IDismissableOverlay
{
    /// <summary>Quiet time after the last drag update before a scrub seek is committed.</summary>
    private const long ScrubSettleMs = 120;

    internal static readonly double[] Speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    private readonly VideoFit _fit;

    /// <summary>The inline view this copy covers the window for, when this is the fullscreen copy.</summary>
    private readonly VideoPlayerView? _fullscreenOf;

    private readonly Stopwatch _sinceScrub = new();

    private AdwButton _fullscreen = null!;
    private VideoPlayerView? _fullscreenCopy;
    private Label _elapsed = null!;
    private AdwButton _mute = null!;
    private Widget _panel = null!;
    private float? _pendingScrub;
    private AdwButton _pip = null!;
    private AdwButton _play = null!;
    private Label _remaining = null!;
    private Widget? _root;
    private Ticker? _scrubTicker;
    private MediaSlider _seek = null!;
    private bool _showRemaining;
    private AdwButton _speed = null!;
    private AnimatedOpacity _spinner = null!;
    private AnimatedOpacity _centre = null!;

    private readonly Icon _centreIcon = new(MaterialIcons.PlayArrow) {
        Size = 40f,
        Color = Colors.White,
    };

    private AnimatedOpacity _holdPill = null!;
    private VideoGestureSurface _surface = null!;
    private Label _status = null!;
    private MediaSlider _volume = null!;

    public VideoPlayerView(VideoPlayer player, VideoFit fit = VideoFit.Contain) : base(player) =>
        _fit = fit;

    private VideoPlayerView(VideoPlayerView inline) : this(
        player: inline.Player,
        fit: VideoFit.Contain
    )
    {
        _fullscreenOf = inline;
        AltText = inline.AltText;
        AllowPictureInPicture = inline.AllowPictureInPicture;
    }

    /// <summary>What a screen reader announces for the picture. See <see cref="VideoView.AltText" />.</summary>
    public string? AltText { get; set; }

    /// <summary>Offer the fullscreen button, F and double-click. On by default.</summary>
    public bool AllowFullscreen { get; set; } = true;

    /// <summary>Offer the picture-in-picture button and P. On by default.</summary>
    public bool AllowPictureInPicture { get; set; } = true;

    public bool IsFullscreen => _fullscreenOf is not null || _fullscreenCopy is not null;

    protected override void OnMount()
    {
        base.OnMount();
        Own(Player.Position.Observe(() => OnUi(SyncTime)));
        Own(Player.Buffered.Observe(() => OnUi(SyncTime)));
        Own(Player.State.Observe(() => OnUi(SyncTransport)));
        Own(Player.Media.Observe(() => OnUi(SyncSource)));
        Own(Player.Volume.Subscribe(_ => OnUi(SyncVolume)));
        Own(Player.Muted.Subscribe(_ => OnUi(SyncVolume)));
        Own(Player.Speed.Subscribe(_ => OnUi(SyncSpeed)));
        Own(PictureInPicture.Current.Observe(() => OnUi(SyncPip)));
    }

    protected override void OnUnmount()
    {
        base.OnUnmount();
        _scrubTicker = null;
    }

    protected override bool HoldControls() =>
        base.HoldControls() || _seek.Dragging || _pendingScrub is not null || PointerOver(_panel);

    protected override Widget Build(BuildContext context)
    {
        if (_root is not null) return _root;

        var white = Colors.White;
        var dim = Colors.White.WithAlpha(0.7f);
        _elapsed =
            new Label(text: "0:00", fontSize: 12f, color: white) { FontWeight = FontWeight.Bold };
        _remaining =
            new Label(text: "0:00", fontSize: 12f, color: dim) { FontWeight = FontWeight.Bold };
        _status = new Label(text: "", fontSize: 12f, color: dim);
        _seek = new MediaSlider {
            OnChanged = OnScrub,
            HoverLabel = f => Clock(f * Player.Duration.TotalSeconds),
            SemanticsLabel = "Seek",
        };
        _volume = new MediaSlider {
            OnChanged = OnVolume,
            SemanticsLabel = "Volume",
        };
        _play = IconButton(
            icon: MaterialIcons.PlayArrow,
            label: "Play",
            onPressed: Refocus(Player.TogglePlayPause)
        );
        _mute = IconButton(
            icon: MaterialIcons.VolumeUp,
            label: "Mute",
            onPressed: Refocus(ToggleMute)
        );
        _speed = new AdwButton(label: "1×", onPressed: OpenSpeedMenu) {
            Style = AdwButtonStyle.Flat,
        };
        _pip = IconButton(
            icon: MaterialIcons.PictureInPictureAlt,
            label: "Picture in picture",
            onPressed: TogglePictureInPicture
        );
        _fullscreen = IconButton(
            icon: MaterialIcons.Fullscreen,
            label: "Fullscreen",
            onPressed: ToggleFullscreen
        );
        _surface = new VideoGestureSurface(this);

        var trailing =
            new Row(crossAxisAlignment: CrossAxisAlignment.Center, mainAxisSize: MainAxisSize.Min) {
                Children = {
                    _status,
                    new SizedBox(8),
                    _speed,
                },
            };
        if (AllowPictureInPicture) trailing.Children.Add(_pip);
        if (AllowFullscreen) trailing.Children.Add(_fullscreen);

        _panel = Pane(
            radius: 22f,
            child: new Padding(
                padding: EdgeInsets.Only(
                    left: 14f,
                    top: 8f,
                    right: 8f,
                    bottom: 6f
                ),
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    mainAxisSize: MainAxisSize.Min
                ) {
                    Children = {
                        new Row(crossAxisAlignment: CrossAxisAlignment.Center, spacing: 10f) {
                            Children = {
                                _elapsed,
                                new Expanded(_seek),
                                new GestureDetector(child: _remaining, onTap: ToggleRemaining),
                                new SizedBox(6),
                            },
                        },
                        new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                            Children = {
                                _play,
                                _mute,
                                new SizedBox(width: 84f, child: _volume),
                                new Spacer(),
                                trailing,
                            },
                        },
                    },
                }
            )
        );
        // HeightFactor 1: as tall as the panel, so the bottom-anchored Positioned really sits at the bottom.
        Controls.Child = new Padding(
            padding: EdgeInsets.All(12f),
            child: new Align {
                Alignment = Alignment.BottomCenter,
                HeightFactor = 1f,
                Child = new ConstrainedBox(
                    constraints: new Constraints(
                        minWidth: 0f,
                        maxWidth: 720f,
                        minHeight: 0f,
                        maxHeight: float.PositiveInfinity
                    ),
                    child: _panel
                ),
            }
        );

        _spinner = new AnimatedOpacity(opacity: 0f, child: new AdwSpinner(48f), duration: 0.2f);

        // The big play button a viewer shows while paused — glass that thickens under the pointer.
        var centreBody = SizedBox.Square(size: 68f, child: new Center(_centreIcon));
        _centre = new AnimatedOpacity(
            opacity: 0f,
            duration: 0.18f,
            child: Glass
                ? LiquidPane.Interactive(
                    pane: LiquidPane.Clear(child: centreBody, radius: 34f, elevation: 6f, adaptive: true),
                    onPressed: Refocus(_surface.TogglePlay),
                    semantics: "Play"
                )
                : new Pressable {
                    Child = Pane(child: centreBody, radius: 34f),
                    OnPressed = Refocus(_surface.TogglePlay),
                    SemanticsLabel = "Play",
                }
        );

        // Press-and-hold says why the video is suddenly fast.
        _holdPill = new AnimatedOpacity(
            opacity: 0f,
            duration: 0.15f,
            child: Pane(
                radius: 18f,
                child: new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: 16f, vertical: 7f),
                    child: new Row(mainAxisSize: MainAxisSize.Min, spacing: 6f) {
                        Children = {
                            new Label(text: "2×", fontSize: 14f, color: Colors.White) {
                                FontWeight = FontWeight.Bold,
                            },
                            new Icon(MaterialIcons.FastForward) {
                                Size = 18f,
                                Color = Colors.White,
                            },
                        },
                    }
                )
            )
        );

        _root = new ThemeProvider(
            data: OsdTheme(ThemeProvider.Of(context)),
            child: new Stack {
                Children = {
                    new VideoView(player: Player, fit: _fit) { AltText = AltText },
                    Positioned.Fill(new Center(_spinner)),
                    Positioned.Fill(_surface),
                    Positioned.Fill(new Center(_centre)),
                    new Positioned(
                        left: 0,
                        right: 0,
                        top: 16,
                        child: new Align {
                            Alignment = Alignment.TopCenter,
                            HeightFactor = 1f,
                            Child = _holdPill,
                        }
                    ),
                    new Positioned(
                        left: 0,
                        right: 0,
                        bottom: 0,
                        child: Controls
                    ),
                },
            }
        );

        // First paint shows the player as it already is, not as it was when this was constructed.
        Built = true;
        SyncSource();
        SyncTransport();
        SyncVolume();
        SyncSpeed();
        SyncPip();
        // The fullscreen copy takes the keyboard as it appears, so Esc and Space work at once.
        if (_fullscreenOf is not null) Owner?.RequestFocus(_surface);
        return _root;
    }

    // ── fine-grained syncs ──────────────────────────────────────────────────────

    private void SyncTime()
    {
        double duration = Player.Duration.TotalSeconds;
        float fraction = _pendingScrub ?? (float)Player.Progress;
        double elapsed = duration > 0 ? fraction * duration : Player.Position.Value.TotalSeconds;

        if (!_seek.Dragging) _seek.Value = fraction;
        SetText(label: _elapsed, value: Clock(elapsed));
        SetText(
            label: _remaining,
            value: Player.Media.Value?.IsLive == true ? "LIVE"
            : _showRemaining ? "−" + Clock(duration - elapsed)
            : Clock(duration)
        );

        if (duration <= 0)
        {
            _seek.SetBuffered(start: 0, end: 0);
            return;
        }

        _seek.SetBuffered(
            start: (float)Player.Progress,
            end: (float)Math.Clamp(
                value: (Player.Position.Value.TotalSeconds + Player.Buffered.Value.TotalSeconds) /
                       duration,
                min: 0,
                max: 1
            )
        );
    }

    private void SyncTransport()
    {
        var state = Player.State.Value;
        bool playing = Player.IsPlaying;
        SetIcon(button: _play, icon: playing ? MaterialIcons.Pause : MaterialIcons.PlayArrow);
        _play.Label = playing ? "Pause" : "Play";
        SetText(
            label: _status,
            value: state switch {
                PlaybackState.Failed => Player.Error.Value ?? "Playback failed",
                PlaybackState.Opening => "Opening…",
                _ => "",
            }
        );
        _spinner.Opacity = state is PlaybackState.Buffering or PlaybackState.Opening ? 1f : 0f;
        bool showCentre = !playing &&
                          state is not PlaybackState.Opening and not PlaybackState.Failed &&
                          !InPictureInPicture;
        _centre.Opacity = showCentre ? 1f : 0f;
        string centreGlyph = state == PlaybackState.Ended
            ? MaterialIcons.Replay
            : MaterialIcons.PlayArrow;
        if (_centreIcon.IconName != centreGlyph)
        {
            _centreIcon.IconName = centreGlyph;
            _centreIcon.MarkNeedsPaint();
        }

        _surface.MarkNeedsPaint(); // the centre play glyph follows the state
    }

    private void SyncSource()
    {
        _seek.Enabled = Player.Media.Value?.IsSeekable ?? false;
        SyncTime(); // a new source means a new duration under the same elapsed time
    }

    private void SyncVolume()
    {
        bool muted = Player.Muted.Value;
        float volume = Player.Volume.Value;
        _volume.Value = muted ? 0 : volume;
        SetIcon(
            button: _mute,
            icon: muted || volume <= 0 ? MaterialIcons.VolumeOff
            : volume < 0.5f ? MaterialIcons.VolumeDown
            : MaterialIcons.VolumeUp
        );
    }

    private void SyncSpeed() => _speed.Label = $"{Player.Speed.Value:0.##}×";

    private void SyncPip()
    {
        bool here = PictureInPicture.Current.Value == Player;
        SetIcon(
            button: _pip,
            icon: here ? MaterialIcons.PictureInPicture : MaterialIcons.PictureInPictureAlt
        );
        _surface.MarkNeedsPaint(); // the "playing in picture-in-picture" card
        SyncTransport(); // the centre button stays out of the way of that card
    }

    internal void SetHolding(bool holding) => _holdPill.Opacity = holding ? 1f : 0f;

    /// <summary>
    ///     Wrap a control's action so the keyboard goes back to the picture after it: a click on
    ///     the mute button should not leave Space toggling mute instead of playback.
    /// </summary>
    private Action Refocus(Action action) => () =>
    {
        action();
        Owner?.RequestFocus(_surface);
    };

    // ── intents ─────────────────────────────────────────────────────────────────

    internal bool InPictureInPicture => PictureInPicture.Current.Value == Player;

    public void ToggleFullscreen()
    {
        if (!AllowFullscreen) return;
        if (_fullscreenOf is not null)
        {
            _fullscreenOf.ExitFullscreen();
            return;
        }

        if (_fullscreenCopy is not null)
        {
            ExitFullscreen();
            return;
        }

        if (Owner is not { } app) return;
        // A second view of the same player covering the window, rather than this one moved: the
        // page underneath keeps its layout and scroll position, and leaving is a pop.
        _fullscreenCopy = new VideoPlayerView(this);
        app.PushOverlay(_fullscreenCopy);
        app.Engine.WindowSetFullscreen(windowId: app.WindowId, on: true);
    }

    /// <summary>Esc (and the Android back button) leaves fullscreen — the overlay copy is the one asked.</summary>
    bool IDismissableOverlay.RequestDismiss()
    {
        if (_fullscreenOf is null) return false;
        ExitFullscreen();
        return true;
    }

    public void ExitFullscreen()
    {
        if (_fullscreenOf is not null)
        {
            _fullscreenOf.ExitFullscreen();
            return;
        }

        if (_fullscreenCopy is null || Owner is not { } app) return;
        app.PopOverlay(_fullscreenCopy);
        _fullscreenCopy = null;
        app.Engine.WindowSetFullscreen(windowId: app.WindowId, on: false);
        app.RequestFocus(_surface);
    }

    public void TogglePictureInPicture()
    {
        if (!AllowPictureInPicture) return;
        if (InPictureInPicture)
        {
            PictureInPicture.Close();
            return;
        }

        ExitFullscreen();
        PictureInPicture.Open(player: Player, from: Owner);
    }

    private void ToggleRemaining()
    {
        _showRemaining = !_showRemaining;
        SyncTime();
    }

    private void OnVolume(float value)
    {
        Player.Muted.Value = false;
        Player.Volume.Value = value;
    }

    private void ToggleMute()
    {
        // Unmuting at zero would be silent; bring the volume back to something audible.
        if (Player.Muted.Value || Player.Volume.Value <= 0f)
        {
            if (Player.Volume.Value <= 0f) Player.Volume.Value = 0.5f;
            Player.Muted.Value = false;
        }
        else Player.Muted.Value = true;
    }

    internal void NudgeVolume(float delta)
    {
        Player.Muted.Value = false;
        Player.Volume.Value = Math.Clamp(value: Player.Volume.Value + delta, min: 0f, max: 1f);
    }

    private void OpenSpeedMenu()
    {
        var items = new List<AdwMenuItem> { AdwMenuItem.Header("Playback Speed") };
        foreach (double s in Speeds)
        {
            double speed = s;
            items.Add(
                AdwMenuItem.Radio(
                    label: speed == 1.0 ? "Normal" : $"{speed:0.##}×",
                    selected: Math.Abs(Player.Speed.Value - speed) < 1e-6,
                    onActivated: () => Player.Speed.Value = speed
                )
            );
        }

        var loop =
            new AdwMenuItem(
                label: "Loop",
                onActivated: () => Player.Loop.Value = !Player.Loop.Value
            ) {
                Role = AdwMenuItemRole.Check,
                Checked = Player.Loop.Value,
            };
        AdwMenuButton.Show(anchor: _speed.Bounds, sections: [items, [loop]], minWidth: 180f);
    }

    /// <summary>
    ///     Dragging emits a change per frame, and each would restart both decoders. The thumb and
    ///     the elapsed time follow the pointer at once; the seek lands once the drag settles.
    /// </summary>
    private void OnScrub(float value)
    {
        _pendingScrub = value;
        _sinceScrub.Restart();
        _scrubTicker ??= CreateTicker(_ => FlushScrub());
        _scrubTicker.Start();
        SyncTime();
    }

    private void FlushScrub()
    {
        if (_pendingScrub is not { } value)
        {
            _scrubTicker?.Stop();
            return;
        }

        if (_sinceScrub.ElapsedMilliseconds < ScrubSettleMs) return;

        _scrubTicker?.Stop();
        _sinceScrub.Reset();
        Player.Seek(Player.Duration * value);

        // Cleared last: until the seek lands, the thumb should stay where the pointer left it
        // rather than snap back to the old position for a frame.
        _pendingScrub = null;
        ShowControls();
    }
}

/// <summary>
///     The layer between the picture and the controls: it takes the gestures, the keyboard, and
///     paints the transient feedback — the centre play glyph, the ±10 s ripple, the 2× pill and
///     the picture-in-picture card. One leaf instead of a widget per effect, because every one of
///     them is a few rectangles and a glyph that come and go together with the pointer.
/// </summary>
internal sealed class VideoGestureSurface(VideoPlayerView view) : Widget
{
    private const long HoldMs = 350;
    private const float HoldSlop = 8f;
    private const float FlashSeconds = 0.6f;
    private const double HoldSpeed = 2.0;

    private readonly Stopwatch _flashClock = new();
    private readonly Stopwatch _pressClock = new();

    private Ticker? _anim;
    private string? _flashGlyph;
    private string? _flashText;
    private float _flashX = 0.5f; // where across the surface the flash sits, 0–1
    private bool _holding;
    private long _lastTapMs;
    private bool _pressed;
    private Offset _pressAt;
    private double _speedBeforeHold;
    private Size _size;

    private VideoPlayer Player => view.Player;

    public override bool Focusable => true;
    public override bool HandlesDirectionalKeys => true;

    protected override void OnMount() => _anim = CreateTicker(_ => Animate());

    protected override void OnUnmount() => _anim = null;

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

    // ── paint ───────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        var center = new Offset(
            x: Bounds.X + (Bounds.Width / 2f),
            y: Bounds.Y + (Bounds.Height / 2f)
        );

        if (view.InPictureInPicture)
        {
            paint.AddRect(bounds: Bounds, color: Colors.Black.WithAlpha(0.7f));
            Icons.Draw(
                paint: paint,
                glyph: MaterialIcons.PictureInPictureAlt,
                box: new Rect(
                    x: center.X - 24f,
                    y: center.Y - 40f,
                    width: 48f,
                    height: 48f
                ),
                color: Colors.White.WithAlpha(0.8f),
                size: 40f
            );
            const string text = "Playing in picture-in-picture";
            var size = UI.TextShaping.TextMeasure.Measure(text: text, fontSize: 13f);
            paint.AddText(
                text: text,
                baselineX: center.X - (size.Width / 2f),
                baselineY: center.Y + 26f,
                color: Colors.White.WithAlpha(0.8f),
                fontSize: 13f
            );
            return;
        }

        if (_flashGlyph is not null)
        {
            float t = (float)_flashClock.Elapsed.TotalSeconds / FlashSeconds;
            float alpha = 1f - (t * t);
            float scale = 1f + (0.25f * t);
            var at = new Offset(x: Bounds.X + (Bounds.Width * _flashX), y: center.Y);
            Disc(
                paint: paint,
                at: at,
                radius: 30f * scale,
                glyph: _flashGlyph,
                alpha: alpha
            );
            if (_flashText is not null)
            {
                paint.PushAlpha(alpha);
                var size = UI.TextShaping.TextMeasure.Measure(
                    text: _flashText,
                    fontSize: 13f,
                    weight: FontWeight.Bold
                );
                paint.AddText(
                    text: _flashText,
                    baselineX: at.X - (size.Width / 2f),
                    baselineY: at.Y + 52f,
                    color: Colors.White,
                    fontSize: 13f,
                    fontWeight: FontWeight.Bold,
                    shadowColor: Colors.Black.WithAlpha(0.6f),
                    shadowBlur: 4f
                );
                paint.PopAlpha();
            }
        }
    }

    private static void Disc(PaintList paint, Offset at, float radius, string glyph, float alpha)
    {
        paint.PushAlpha(alpha);
        paint.AddRect(
            bounds: new Rect(
                x: at.X - radius,
                y: at.Y - radius,
                width: radius * 2f,
                height: radius * 2f
            ),
            color: Colors.Black.WithAlpha(0.5f),
            radius: radius
        );
        float glyphSize = radius * 1.2f;
        Icons.Draw(
            paint: paint,
            glyph: glyph,
            box: new Rect(
                x: at.X - radius,
                y: at.Y - radius,
                width: radius * 2f,
                height: radius * 2f
            ),
            color: Colors.White,
            size: glyphSize
        );
        paint.PopAlpha();
    }

    private void Flash(string glyph, string? text = null, float x = 0.5f)
    {
        _flashGlyph = glyph;
        _flashText = text;
        _flashX = x;
        _flashClock.Restart();
        _anim?.Start();
        MarkNeedsPaint();
    }

    /// <summary>One ticker for both transient effects: the flash fade and the hold timer.</summary>
    private void Animate()
    {
        if (_pressed && !_holding && _pressClock.ElapsedMilliseconds >= HoldMs) BeginHold();

        if (_flashGlyph is not null)
        {
            if (_flashClock.Elapsed.TotalSeconds >= FlashSeconds)
            {
                _flashGlyph = null;
                _flashText = null;
            }

            MarkNeedsPaint();
        }

        if (_flashGlyph is null && !_pressed) _anim?.Stop();
    }

    // ── pointer ─────────────────────────────────────────────────────────────────

    public override MouseCursor? GetCursor(Offset point) => MouseCursor.Default;

    public override void OnPointerMove(Offset point)
    {
        if (!App.PointerIsTouch) view.ShowControls();
        if (_pressed && !_holding && Distance(a: point, b: _pressAt) > HoldSlop)
            _pressed = false; // moved off: neither a tap nor a hold
    }

    public override void OnPointerDown(Offset point)
    {
        if (view.InPictureInPicture) return;
        _pressed = true;
        _pressAt = point;
        _pressClock.Restart();
        _anim?.Start();
    }

    public override void OnPointerUp(Offset point)
    {
        if (view.InPictureInPicture)
        {
            PictureInPicture.Close(); // the card is the "bring it back" button
            return;
        }

        if (_holding)
        {
            EndHold();
            return;
        }

        if (!_pressed) return;
        _pressed = false;

        long now = Environment.TickCount64;
        bool second = now - _lastTapMs < 300;
        _lastTapMs = second ? 0 : now;
        float side = Bounds.Width > 0 ? (point.X - Bounds.X) / Bounds.Width : 0.5f;

        if (App.PointerIsTouch)
        {
            // Touch: a tap reveals or dismisses the controls; a double-tap on a side seeks.
            if (second && side is < 0.35f or > 0.65f) SeekBy(seconds: side < 0.5f ? -10 : 10);
            else if (!second && view.ControlsVisible && Player.IsPlaying) view.HideControls();
            else view.ShowControls();
            return;
        }

        // Mouse: a click plays or pauses; the second click of a double one undoes that and goes
        // fullscreen instead, so a double-click leaves playback as it found it.
        TogglePlay();
        if (second && view.AllowFullscreen) view.ToggleFullscreen();
    }

    public override void OnPointerCancel()
    {
        if (_holding) EndHold();
        _pressed = false;
    }

    // Swallow the base long-press → right-click mapping: holding here means 2×, handled above.
    public override void OnLongPress(Offset point) { }

    private void BeginHold()
    {
        if (!Player.IsPlaying) return;
        _holding = true;
        _speedBeforeHold = Player.Speed.Value;
        Player.Speed.Value = HoldSpeed;
        view.HideControls();
        view.SetHolding(true);
    }

    private void EndHold()
    {
        _holding = false;
        _pressed = false;
        Player.Speed.Value = _speedBeforeHold;
        view.SetHolding(false);
    }

    internal void TogglePlay()
    {
        bool wasPlaying = Player.IsPlaying;
        Player.TogglePlayPause();
        Flash(wasPlaying ? MaterialIcons.Pause : MaterialIcons.PlayArrow);
        view.ShowControls();
    }

    private void SeekBy(int seconds)
    {
        if (Player.Media.Value?.IsSeekable != true) return;
        Player.SeekBy(TimeSpan.FromSeconds(seconds));
        Flash(
            glyph: seconds < 0 ? MaterialIcons.Replay10 : MaterialIcons.Forward10,
            text: seconds < 0 ? $"−{-seconds} s" : $"+{seconds} s",
            x: seconds < 0 ? 0.2f : 0.8f
        );
    }

    // ── keyboard ────────────────────────────────────────────────────────────────

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || mods != Modifiers.None) return;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Space or KeyCode.K:
                TogglePlay();
                break;
            case KeyCode.Left:
                SeekBy(-5);
                break;
            case KeyCode.Right:
                SeekBy(5);
                break;
            case KeyCode.J:
                SeekBy(-10);
                break;
            case KeyCode.L:
                SeekBy(10);
                break;
            case KeyCode.Up:
                view.NudgeVolume(0.1f);
                view.ShowControls();
                break;
            case KeyCode.Down:
                view.NudgeVolume(-0.1f);
                view.ShowControls();
                break;
            case KeyCode.M:
                Player.Muted.Value = !Player.Muted.Value;
                Flash(Player.Muted.Value ? MaterialIcons.VolumeOff : MaterialIcons.VolumeUp);
                break;
            case KeyCode.F:
                view.ToggleFullscreen();
                break;
            case KeyCode.P:
                view.TogglePictureInPicture();
                break;
            case >= KeyCode.Digit1 and <= KeyCode.Digit0
                when Player.Media.Value?.IsSeekable == true:
                // 1–9 jump to 10–90 %, 0 to the start — the number row as a chapter bar.
                int tenth = scancode == (uint)KeyCode.Digit0
                    ? 0
                    : (int)(scancode - (uint)KeyCode.Digit1) + 1;
                Player.Seek(Player.Duration * (tenth / 10.0));
                view.ShowControls();
                break;
        }
    }

    private static float Distance(Offset a, Offset b) =>
        MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
