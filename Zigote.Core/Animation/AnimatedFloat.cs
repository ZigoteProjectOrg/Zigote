namespace Zigote.Core.Animation;

/// <summary>
///     One number that animates to wherever you point it, and stops when it gets there. The
///     smallest useful thing built on <see cref="Motion" />: a <see cref="Ticker" /> to advance it,
///     a callback to mark whatever it drives dirty, and nothing else.
///     <para>
///         It exists because the alternative — an <see cref="AnimationController" /> plus a
///         <c>from</c>/<c>to</c>/<c>started</c> triple, retargeted by hand on the way into paint —
///         was written out three times over (a segmented control's pill, a tab bar's underline, a
///         toggle group's selection) and is the same six lines every time. With a spring spec the
///         retarget also keeps the velocity, which is the behaviour those three all wanted and none
///         of them had: a selection changed mid-slide continues rather than restarting.
///     </para>
///     <para>
///         Widgets create one per animated quantity, point it at a target whenever their state
///         changes, and read <see cref="Value" /> while painting. Call
///         <see cref="AttachTicker" /> from <c>OnMount</c> — the ticker is mount-scoped, like every
///         other one.
///     </para>
/// </summary>
public sealed class AnimatedFloat
{
    private readonly Action _onChanged;
    private Motion _motion;
    private Ticker? _ticker;

    /// <param name="spec">How it travels. A spring survives being retargeted mid-flight.</param>
    /// <param name="onChanged">
    ///     Raised on every tick: mark the owner for paint (or layout, if the value moves geometry).
    /// </param>
    /// <param name="value">Where it starts, un-animated.</param>
    public AnimatedFloat(MotionSpec spec, Action onChanged, float value = 0f)
    {
        _onChanged = onChanged;
        _motion = new Motion(spec: spec, value: value, target: value);
    }

    /// <summary>Where it is now — the value to draw with.</summary>
    public float Value => _motion.Value;

    /// <summary>Where it is heading.</summary>
    public float Target => _motion.Target;

    /// <summary>Units per second, signed. Non-zero only under a spring or decay spec.</summary>
    public float Velocity => _motion.Velocity;

    /// <summary>True while it is still travelling — and therefore still holding a ticker.</summary>
    public bool IsMoving => !_motion.IsSettled;

    public MotionSpec Spec
    {
        get => _motion.Spec;
        set => _motion.Spec = value;
    }

    /// <summary>Animate to <paramref name="target" />, keeping whatever speed it already had.</summary>
    public void To(float target)
    {
        if (MathF.Abs(target - _motion.Target) < 1e-6f) return;
        _motion.Retarget(target);
        // No RequestFrame here: a running ticker is what keeps the frame loop awake
        // (Ticker.AnyActive), and the tick callback is what marks the damage.
        _ticker?.Start();
    }

    /// <summary>Jump there and stop — a first paint, or a change that must not be seen moving.</summary>
    public void SnapTo(float value)
    {
        _motion.SnapTo(value);
        _ticker?.Stop();
    }

    /// <summary>
    ///     (Re)bind the driving ticker from <paramref name="vsync" /> — call it from the owning
    ///     widget's <c>OnMount</c>. A detach disposed the previous one, and a value retargeted while
    ///     unmounted resumes here.
    /// </summary>
    public void AttachTicker(ITickerProvider vsync)
    {
        _ticker = vsync.CreateTicker(Tick);
        if (IsMoving) _ticker.Start();
    }

    private void Tick(float dt)
    {
        if (!_motion.Tick(dt)) _ticker?.Stop();
        _onChanged();
    }
}
