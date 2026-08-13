using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Base class for widgets that smoothly animate to a new value whenever a property changes — the
///     foundation for <c>AnimatedOpacity</c>, <c>AnimatedAlign</c>, <c>AnimatedPadding</c> and
///     friends.
///     <para>
///         It owns an <see cref="AnimationController" /> driven by its own <see cref="Ticker" /> (so
///         it
///         works without a vsync provider) and runs a 0→1 pass each time <see cref="Animate" /> is
///         called. Subclasses snapshot their "from" value, set the new "to", call
///         <see cref="Animate" />,
///         and read <see cref="Progress" /> to interpolate. The ticker stops itself when the animation
///         settles, so an idle implicit-animation widget costs nothing.
///     </para>
/// </summary>
public abstract class ImplicitlyAnimatedWidget : Widget
{
    protected readonly AnimationController Controller;
    private Ticker _ticker;

    protected ImplicitlyAnimatedWidget(float durationSeconds = 0.25f,
        Func<float, float>? curve = null)
    {
        Controller = new AnimationController(durationSeconds) { Curve = curve ?? Curves.EaseOut };
        Controller.OnTick += OnControllerTick;
        _ticker = new Ticker(Step);
    }

    /// <summary>The eased animation progress in [0,1] for the in-flight transition.</summary>
    protected float Progress => Controller.Value;

    /// <summary>Animation duration in seconds.</summary>
    public float Duration
    {
        get => Controller.Duration;
        set => Controller.Duration = value;
    }

    /// <summary>Restart the 0→1 transition (call after updating the from/to values).</summary>
    protected void Animate()
    {
        Controller.Dismiss();
        Controller.Forward();
        _ticker.Start();
    }

    private void Step(float dt)
    {
        Controller.Tick(dt);
        if (Controller.Status is AnimationStatus.Completed or AnimationStatus.Dismissed)
            _ticker.Stop();
    }

    private void OnControllerTick()
    {
        // The animated value affects size for some subclasses (padding/align), so relayout + repaint.
        MarkNeedsLayout();
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        // Detach unsubscribed the tick handler as well as disposing the ticker — restore BOTH, or a
        // re-attached widget animates without ever asking for a frame: the controller advances, the
        // painted progress never changes, and the subtree only appears once something else (a
        // resize) forces a relayout. That is every implicit animation inside a container that
        // unmounts and remounts, e.g. a split view's content pane folding on a narrow window.
        Controller.OnTick -= OnControllerTick;
        Controller.OnTick += OnControllerTick;
        _ticker.Dispose();
        _ticker = new Ticker(Step);
        if (Controller.Status is AnimationStatus.Forward or AnimationStatus.Reverse)
            _ticker.Start();
    }

    public override void Detach()
    {
        base.Detach();
        Controller.OnTick -= OnControllerTick;
        _ticker.Dispose();
    }
}
