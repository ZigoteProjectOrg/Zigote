using System.Diagnostics;
using Zigote.UI.Widgets.Layout;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.State;
using Zigote.UI.Adwaita;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.Videoplayer;

/// <summary>
///     What the full player and the picture-in-picture view share: on-video controls that fade in
///     on activity and out again once playback has the screen to itself, and a safe way to touch
///     widgets from the player's signals.
///     <para>
///         Retained and fine-grained, like the rest of this package: a subclass builds its tree
///         once and each player signal writes only into the widgets it can affect, so a position
///         that moves every frame retouches one label rather than a rebuilt subtree.
///     </para>
/// </summary>
public abstract class VideoChrome : ComposedWidget
{
    private readonly Stopwatch _sinceActivity = Stopwatch.StartNew();
    private Ticker? _hideTicker;
    private int _uiThread;

    protected VideoChrome(VideoPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Player = player;
    }

    public VideoPlayer Player { get; }

    /// <summary>How long the controls linger after the last pointer movement or key.</summary>
    public TimeSpan AutoHideDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The faded layer holding every control. Subclasses put their chrome in it.</summary>
    protected AnimatedOpacity Controls { get; } = new(opacity: 1f, duration: 0.2f);

    public bool ControlsVisible => Controls.Opacity > 0f;

    /// <summary>Set by a subclass once its controls exist; signal syncs are dropped until then.</summary>
    protected bool Built { get; set; }

    /// <summary>
    ///     Video chrome is read against the picture, which is dark whatever the app's appearance —
    ///     so the controls always get a dark theme, keeping the app's own when it already is one.
    /// </summary>
    protected static ThemeData OsdTheme(ThemeData app) => app.IsDark ? app : AdwTheme.Dark;

    /// <summary>
    ///     True while the controls must stay up regardless of the timer: paused, scrubbing, the
    ///     pointer resting on them.
    /// </summary>
    protected virtual bool HoldControls() => !Player.IsPlaying;

    protected override void OnMount()
    {
        _uiThread = Environment.CurrentManagedThreadId;
        _hideTicker = CreateTicker(_ => MaybeHide());
        // Stopping brings the controls back; starting doesn't — a seek or a speed change restarts the
        // pipeline through Buffering → Playing, and that is not activity worth surfacing them for.
        Own(Player.State.Observe(() => OnUi(() =>
                    {
                        if (!Player.IsPlaying) ShowControls();
                    }
                )
            )
        );
        ShowControls();
    }

    // The ticker is owned (disposed on unmount); drop the handle so a re-mount makes a new one.
    protected override void OnUnmount() => _hideTicker = null;

    /// <summary>Bring the controls up and restart the hide timer — call on any user activity.</summary>
    public void ShowControls()
    {
        _sinceActivity.Restart();
        Controls.Opacity = 1f;
        _hideTicker?.Start();
    }

    public void HideControls()
    {
        Controls.Opacity = 0f;
        _hideTicker?.Stop();
    }

    private void MaybeHide()
    {
        if (_sinceActivity.Elapsed < AutoHideDelay) return;
        // Held: stop polling. The next activity — a state change, a pointer move — restarts it.
        if (HoldControls()) _hideTicker?.Stop();
        else HideControls();
    }

    /// <summary>Is the pointer resting inside <paramref name="w" />?</summary>
    protected bool PointerOver(Widget w) =>
        Owner is { } app && w.Bounds.Contains(px: app.PointerPosition.X, py: app.PointerPosition.Y);

    /// <summary>
    ///     Signals are written wherever the writer happens to be — <see cref="VideoPlayer.Tick" /> is
    ///     on the frame loop, but a failed open lands on whatever thread awaited it. Widget mutation
    ///     belongs on the UI thread either way.
    /// </summary>
    protected void OnUi(Action action)
    {
        // Mount comes before the first build, and the player keeps talking in between.
        if (!Built) return;
        if (Environment.CurrentManagedThreadId == _uiThread) action();
        else App.Active?.Post(action);
    }

    /// <summary>Label re-lays out on assignment, so only assign when it would say something new.</summary>
    protected static void SetText(Label label, string value)
    {
        if (label.Text != value) label.Text = value;
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

    /// <summary>
    ///     Draw the controls on Liquid Glass (the default) or on a flat dark fill. Glass is a backdrop
    ///     capture per visible pane every frame the video moves, so an app with a "reduce
    ///     transparency" preference should wire it here. Read at build time.
    /// </summary>
    public bool Glass { get; set; } = true;

    /// <summary>
    ///     The material a floating control sits on: clear glass, which adapts per pixel to the
    ///     frame under it — or, with <see cref="Glass" /> off, a dark translucent fill.
    /// </summary>
    protected Widget Pane(Widget child, float radius)
    {
        if (Glass) return LiquidPane.Clear(child: child, radius: radius, elevation: 4f);
        return new DecoratedBox { Fill = Colors.Black.WithAlpha(0.55f), Radius = radius, Child = child };
    }

    /// <summary>A flat round icon button, the only kind on-video chrome uses.</summary>
    protected static AdwButton IconButton(string icon, string label, Action onPressed) =>
        new(label: label, onPressed: onPressed) {
            IconName = icon,
            Style = AdwButtonStyle.Flat,
            Circular = true,
        };

    protected static void SetIcon(AdwButton button, string icon)
    {
        if (button.IconName != icon) button.IconName = icon;
    }
}
