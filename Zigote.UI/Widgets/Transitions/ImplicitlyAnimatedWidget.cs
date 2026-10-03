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
        Func<float, float>? curve = null, MotionSpec? spec = null)
    {
        Controller = new AnimationController(durationSeconds) {
            Curve = curve ?? Curves.EaseOut,
            Spec = spec,
        };
        Controller.OnTick += OnControllerTick;
        _ticker = new Ticker(Step);
    }

    /// <summary>The eased animation progress in [0,1] for the in-flight transition.</summary>
    protected float Progress => Controller.Value;

    /// <summary>
    ///     Drive the transition with a spring instead of a duration and curve. Worth it for anything
    ///     whose target can change while it is still moving — a panel that folds while it is still
    ///     unfolding arrives without the kink a restarted curve leaves.
    /// </summary>
    public MotionSpec? Spec
    {
        get => Controller.Spec;
        set => Controller.Spec = value;
    }

    /// <summary>
    ///     Let a bouncy <see cref="Spec" /> carry the interpolation past its target and back. Off by
    ///     default: overshoot is a deliberate look, not something a widget should start doing
    ///     because someone picked a springy spec.
    /// </summary>
    public bool AllowOvershoot
    {
        get => !Controller.ClampProgress;
        set => Controller.ClampProgress = !value;
    }

    /// <summary>Animation duration in seconds.</summary>
    public float Duration
    {
        get => Controller.Duration;
        set => Controller.Duration = value;
    }

    /// <summary>Restart the 0→1 transition (call after updating the from/to values).</summary>
    protected void Animate()
    {
        // Subclasses re-snapshot their "from" at the current interpolated value, so restarting
        // progress at 0 is continuous in position. Carrying the spring's velocity across the
        // restart makes it continuous in speed too.
        // ponytail: carried in progress space, not rescaled by the change in from→to distance —
        // the base class does not know the value type. Rescale if a large retarget ever kinks.
        float carried = Controller.Spec is null ? 0f : Controller.Velocity;
        Controller.Dismiss();
        Controller.Forward();
        if (carried != 0f) Controller.Velocity = carried;
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
        base.Attach(owner: owner, parent: parent);
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
