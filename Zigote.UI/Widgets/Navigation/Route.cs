using Zigote.Core;
using Zigote.Core.Animation;

namespace Zigote.UI.Widgets.Navigation;

/// <summary>Builds the content widget for a route, given the ambient build context.</summary>
public delegate Widget WidgetBuilder(BuildContext context);

/// <summary>Generates a <see cref="Route" /> for a set of <see cref="RouteSettings" />, or null.</summary>
public delegate Route? RouteFactory(RouteSettings settings);

/// <summary>Lifecycle phase of a route within the <see cref="NavigatorState" /> stack.</summary>
public enum RouteStatus
{
    /// <summary>Animating in after a push.</summary>
    Pushing,

    /// <summary>Settled and fully visible.</summary>
    Idle,

    /// <summary>Animating out before removal.</summary>
    Popping,
}

/// <summary>
///     A single entry in a <see cref="NavigatorState" />'s history stack. A route owns its
///     (lazily-built, retained) content widget and an
///     <see cref="AnimationController" /> that drives its enter/exit transition.
///     <para>
///         Concrete user-facing routes derive from <see cref="PageRoute{T}" /> /
///         <see cref="MaterialPageRoute{T}" />. The generic <see cref="Route{T}" /> adds a typed
///         result completed when the route is popped.
///     </para>
/// </summary>
public abstract class Route
{
    private readonly Ticker _ticker;

    protected Route()
    {
        Transition = new AnimationController(0.30f) { Curve = Curves.EaseOut };
        _ticker = new Ticker(Step);
    }

    /// <summary>The navigator this route is installed in, or null if detached.</summary>
    public NavigatorState? Navigator { get; internal set; }

    /// <summary>Name + arguments this route was created with.</summary>
    public RouteSettings Settings { get; internal set; } = RouteSettings.Empty;

    /// <summary>True when this route was created from a declarative <see cref="Page" /> (Navigator 2.0).</summary>
    public bool IsPageBased { get; internal set; }

    /// <summary>Drives the 0→1 enter and 1→0 exit transition.</summary>
    public AnimationController Transition { get; }

    /// <summary>Current lifecycle phase.</summary>
    public RouteStatus Status { get; internal set; } = RouteStatus.Pushing;

    /// <summary>True if this is the topmost route currently receiving input.</summary>
    public bool IsCurrent => Navigator?.CurrentRoute == this;

    /// <summary>True while installed in a navigator.</summary>
    public bool IsActive => Navigator is not null;

    /// <summary>Seconds the enter/exit transition runs. Override to customise or disable (0).</summary>
    public virtual float TransitionDuration => 0.30f;

    /// <summary>
    ///     When true (default) this route fully covers routes below it once settled, so they are not
    ///     painted. Set false for translucent routes (e.g. dialogs) that should show the page behind.
    /// </summary>
    public virtual bool Opaque => true;

    // ── Page reference (declarative) ──────────────────────────────────────────
    internal Page? SourcePage { get; set; }

    // ── Navigator wiring ──────────────────────────────────────────────────────
    internal Action? OnVisualUpdate { get; set; }
    internal Action? OnExitComplete { get; set; }
    internal Action? OnEntered { get; set; }
    internal object? PendingResult { get; set; }

    internal Widget? ContentOrNull { get; private set; }

    /// <summary>Build (once) and return this route's content widget.</summary>
    internal Widget EnsureContent(BuildContext context)
    {
        return ContentOrNull ??= BuildContent(context);
    }

    /// <summary>Compose the content widget for this route. Called once; the result is retained.</summary>
    protected abstract Widget BuildContent(BuildContext context);

    // ── Transition appearance (overridden by PageRoute) ───────────────────────

    /// <summary>
    ///     Layout offset applied to the content at eased progress <paramref name="t" /> (1 =
    ///     settled).
    /// </summary>
    public virtual Offset TransitionOffset(Size size, float t)
    {
        return Offset.Zero;
    }

    /// <summary>
    ///     Paint opacity applied to the content at eased progress <paramref name="t" /> (1 =
    ///     settled).
    /// </summary>
    public virtual float TransitionOpacity(float t)
    {
        return 1f;
    }

    // ── Transition driving ────────────────────────────────────────────────────

    internal void StartEnter(bool animate)
    {
        Transition.Duration = TransitionDuration;
        if (!animate || TransitionDuration <= 0f)
        {
            Transition.Complete();
            Status = RouteStatus.Idle;
            OnEntered?.Invoke();
            return;
        }

        Status = RouteStatus.Pushing;
        Transition.Dismiss();
        Transition.Forward();
        _ticker.Start();
    }

    internal void StartExit()
    {
        Status = RouteStatus.Popping;
        if (TransitionDuration <= 0f)
        {
            Transition.Dismiss();
            OnExitComplete?.Invoke();
            return;
        }

        Transition.Reverse();
        _ticker.Start();
    }

    private void Step(float dt)
    {
        Transition.Tick(dt);
        OnVisualUpdate?.Invoke();

        switch (Status)
        {
            case RouteStatus.Pushing when Transition.Status == AnimationStatus.Completed:
                Status = RouteStatus.Idle;
                _ticker.Stop();
                OnEntered?.Invoke();
                break;
            case RouteStatus.Popping when Transition.Status == AnimationStatus.Dismissed:
                _ticker.Stop();
                OnExitComplete?.Invoke();
                break;
        }
    }

    /// <summary>Complete the typed result of this route (no-op on the non-generic base).</summary>
    internal abstract void CompleteWith(object? result);

    internal void DisposeRoute()
    {
        _ticker.Dispose();
        Navigator = null;
        OnVisualUpdate = OnExitComplete = OnEntered = null;
    }
}

/// <summary>
///     A <see cref="Route" /> that returns a typed result of type <typeparamref name="T" /> when
///     popped.
///     <see cref="Popped" /> completes with the value passed to <c>Navigator.Pop(result)</c> (or
///     <c>default</c> if popped without one).
/// </summary>
public abstract class Route<T> : Route
{
    private readonly TaskCompletionSource<T?> _completer =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when this route is popped, carrying the pop result.</summary>
    public Task<T?> Popped => _completer.Task;

    /// <summary>Complete this route's result early (without animating it out).</summary>
    public void Complete(T? result = default)
    {
        _completer.TrySetResult(result);
    }

    internal override void CompleteWith(object? result)
    {
        _completer.TrySetResult(result is T typed ? typed : default);
    }
}