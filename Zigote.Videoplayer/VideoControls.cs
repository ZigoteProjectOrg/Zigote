using System.Diagnostics;
using Zigote.Core.Animation;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.Videoplayer;

/// <summary>
///     A transport bar for a <see cref="VideoPlayer" />: play/pause, a scrubber, elapsed and total
///     time, mute and volume, and a speed cycle. It reads the player's signals rather than being
///     told, so it stays right when playback is driven from somewhere else — a keyboard shortcut, a
///     playlist, another copy of these controls.
///     <para>
///         <b>Retained and fine-grained.</b> The bar is built once. Each of the player's signals is
///         subscribed separately and writes into only the widgets that signal can affect: a position
///         that moves every frame retouches one label, not a rebuilt subtree — so nothing above it
///         re-measures and nothing beside it loses its state.
///     </para>
///     <para>
///         Scrubbing is coalesced: dragging emits a change per frame, and each one would otherwise
///         restart both decoders. The thumb and the elapsed time follow the finger immediately; the
///         seek lands once the drag settles.
///     </para>
/// </summary>
public sealed class VideoControls : ComposedWidget
{
    /// <summary>Quiet time after the last drag update before the seek is committed.</summary>
    private const long ScrubSettleMs = 120;

    /// <summary>The rates the speed button cycles through — the set a video player actually needs.</summary>
    private static readonly double[] Speeds = [0.5, 1.0, 1.25, 1.5, 2.0];

    private readonly BufferedBar _bufferBar = new();
    private readonly Icon _muteGlyph = new(MaterialIcons.VolumeUp);
    private readonly Icon _playGlyph = new(MaterialIcons.PlayArrow);
    private readonly Slider _seekBar;
    private readonly Stopwatch _sinceScrub = new();
    private readonly Text _speedLabel;
    private readonly Text _statusLabel;
    private readonly Text _timeLabel;
    private readonly Slider _volumeBar;

    /// <summary>Where the thumb is during a drag, before the seek is committed. Null when not scrubbing.</summary>
    private float? _pendingScrub;

    private Widget? _root;

    /// <summary>Ticker for the scrub debounce only — it runs during a drag and stops after it.</summary>
    private Ticker? _scrubTicker;

    private int _uiThread;

    public VideoControls(VideoPlayer player)
    {
        Player = player;
        _timeLabel = new Text("0:00 / 0:00", new TextStyle(13));
        _statusLabel = new Text("", new TextStyle(12));
        _speedLabel = new Text("1×");
        _seekBar = new Slider(0, 0, 1, OnScrub);
        _volumeBar = new Slider(1, 0, 1, OnVolume);
    }

    internal VideoPlayer Player { get; }

    private VideoPlayer P => Player;

    protected override void OnMount()
    {
        _uiThread = Environment.CurrentManagedThreadId;

        // One subscription per signal, each writing only what that signal can change. Position fires
        // every frame during playback and reaches exactly one label. Own() ties each to the mount
        // period — the player outlives these controls, and its signals hold observers strongly.
        Own(P.Position.Observe(() => OnUi(SyncTime)));
        Own(P.Buffered.Observe(() => OnUi(SyncBuffer)));
        Own(P.State.Observe(() => OnUi(SyncTransport)));
        Own(P.Media.Observe(() => OnUi(SyncSource)));
        Own(P.Volume.Subscribe(_ => OnUi(SyncVolume)));
        Own(P.Muted.Subscribe(_ => OnUi(SyncVolume)));
        Own(P.Speed.Subscribe(_ => OnUi(SyncSpeed)));
    }

    // The ticker itself is owned (disposed on unmount); drop the handle so a re-mount makes a new one.
    protected override void OnUnmount()
    {
        _scrubTicker = null;
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        _timeLabel.Color = theme.TextSecondary;
        _statusLabel.Color = theme.TextSecondary;

        if (_root is not null) return _root;

        _root = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children:
            [
                _seekBar,
                _bufferBar,
                new Row(
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children:
                    [
                        new IconButton(_playGlyph, P.TogglePlayPause, tooltip: "Play / pause"),
                        new IconButton(
                            new Icon(MaterialIcons.Replay),
                            () => P.Seek(TimeSpan.Zero),
                            tooltip: "Restart"
                        ),
                        new SizedBox(8),
                        _timeLabel,
                        new Spacer(),
                        _statusLabel,
                        new SizedBox(8),
                        new TextButton(_speedLabel, CycleSpeed),
                        new IconButton(_muteGlyph, ToggleMute, tooltip: "Mute"),
                        new SizedBox(100, child: _volumeBar),
                    ]
                ),
            ]
        );

        // First paint shows the player as it already is, not as it was when this was constructed.
        SyncTime();
        SyncBuffer();
        SyncTransport();
        SyncSource();
        SyncVolume();
        SyncSpeed();
        return _root;
    }

    // ── fine-grained syncs ──────────────────────────────────────────────────────

    private void SyncTime()
    {
        var duration = P.Duration.TotalSeconds;
        var fraction = _pendingScrub ?? (float)P.Progress;
        var elapsed = duration > 0 ? fraction * duration : P.Position.Value.TotalSeconds;

        _seekBar.Value = fraction;
        SetText(_timeLabel, $"{Clock(elapsed)} / {Clock(duration)}");
    }

    /// <summary>
    ///     The pale read-ahead bar: from the play head to wherever decoding has reached. On a local
    ///     file it sits a comfortable margin ahead and never moves; on a stream it visibly shrinks
    ///     before a stall, which is the point of showing it.
    /// </summary>
    private void SyncBuffer()
    {
        var duration = P.Duration.TotalSeconds;
        if (duration <= 0)
        {
            _bufferBar.Set(0, 0);
            return;
        }

        var start = (float)P.Progress;
        var end = (float)Math.Clamp(
            (P.Position.Value.TotalSeconds + P.Buffered.Value.TotalSeconds) / duration,
            0,
            1
        );
        _bufferBar.Set(start, end);
    }

    private void SyncTransport()
    {
        var state = P.State.Value;
        SetGlyph(_playGlyph, PlayGlyph(state));
        SetText(_statusLabel, StatusLabel(state));
    }

    private void SyncSource()
    {
        _seekBar.Enabled = P.Media.Value?.IsSeekable ?? false;
        SyncTime(); // a new source means a new duration under the same elapsed time
    }

    private void SyncVolume()
    {
        var muted = P.Muted.Value;
        _volumeBar.Value = muted ? 0 : P.Volume.Value;
        SetGlyph(_muteGlyph, muted ? MaterialIcons.VolumeOff : MaterialIcons.VolumeUp);
    }

    private void SyncSpeed()
    {
        SetText(_speedLabel, $"{P.Speed.Value:0.##}×");
    }

    /// <summary>Label re-lays out on assignment, so only assign when it would say something new.</summary>
    private static void SetText(Label label, string value)
    {
        if (label.Text != value) label.Text = value;
    }

    /// <summary>
    ///     <see cref="Icon.IconName" /> is a plain property — swapping the glyph invalidates nothing on
    ///     its own, and the icon is the same size either way, so a repaint is the whole cost.
    /// </summary>
    private static void SetGlyph(Icon icon, string glyph)
    {
        if (icon.IconName == glyph) return;
        icon.IconName = glyph;
        icon.MarkNeedsPaint();
    }

    /// <summary>
    ///     Signals are written wherever the writer happens to be — <see cref="VideoPlayer.Tick" /> is
    ///     on the frame loop, but a failed open lands on whatever thread awaited it. Widget mutation
    ///     belongs on the UI thread either way.
    /// </summary>
    private void OnUi(Action action)
    {
        if (Environment.CurrentManagedThreadId == _uiThread) action();
        else App.Active?.Post(action);
    }

    private static string PlayGlyph(PlaybackState state)
    {
        return state switch
        {
            PlaybackState.Playing or PlaybackState.Buffering => MaterialIcons.Pause,
            _ => MaterialIcons.PlayArrow,
        };
    }

    private static string StatusLabel(PlaybackState state)
    {
        return state switch
        {
            PlaybackState.Buffering => "Buffering…",
            PlaybackState.Ended => "Ended",
            PlaybackState.Failed => "Failed",
            PlaybackState.Opening => "Opening…",
            _ => "",
        };
    }

    // ── intents ─────────────────────────────────────────────────────────────────

    private void OnVolume(float value)
    {
        P.Muted.Value = false;
        P.Volume.Value = value;
    }

    private void ToggleMute()
    {
        P.Muted.Value = !P.Muted.Value;
    }

    private void CycleSpeed()
    {
        var index = Array.FindIndex(Speeds, s => Math.Abs(s - P.Speed.Value) < 1e-6);
        P.Speed.Value = Speeds[(index + 1) % Speeds.Length];
    }

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
        P.Seek(P.Duration * value);

        // Cleared last: until the seek lands, the thumb should stay where the finger left it rather
        // than snap back to the old position for a frame.
        _pendingScrub = null;
    }

    /// <summary>
    ///     A two-pixel strip under the scrubber showing the decoded read-ahead as a span, not a
    ///     length: it starts at the play head, so what it draws is how much runway is left.
    /// </summary>
    private sealed class BufferedBar : Widget
    {
        private const float Thickness = 2f;

        private float _end;
        private Size _size;
        private float _start;

        public void Set(float start, float end)
        {
            if (Math.Abs(start - _start) < 0.001f && Math.Abs(end - _end) < 0.001f) return;
            _start = start;
            _end = end;
            MarkNeedsPaint();
        }

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(float.PositiveInfinity, Thickness));
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
        }

        public override void Paint(PaintList paint)
        {
            if (_end <= _start || Bounds.Width <= 0) return;

            var theme = ThemeProvider.Of(BuildContext.Current);
            var x = Bounds.X + Bounds.Width * _start;
            var width = Bounds.Width * (_end - _start);
            paint.AddRect(
                new Rect(x, Bounds.Y, width, Bounds.Height),
                theme.TextSecondary.WithAlpha(0.35f),
                Thickness / 2f
            );
        }
    }

    /// <summary>h:mm:ss for anything an hour or longer, m:ss below — how a player shows time.</summary>
    internal static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "0:00";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
