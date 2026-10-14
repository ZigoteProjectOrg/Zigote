namespace Zigote.Core.Animation;

/// <summary>How a <see cref="Motion" /> travels toward its target.</summary>
public enum MotionKind
{
    /// <summary>Fixed duration through an easing curve. Not velocity-continuous on retarget.</summary>
    Duration,

    /// <summary>Damped harmonic oscillator. Absorbs an incoming velocity; overshoots when bouncy.</summary>
    Spring,

    /// <summary>Exponential velocity decay with no target — the fling model.</summary>
    Decay,
}

/// <summary>
///     Immutable description of how a value should travel: cheap to copy, safe to hold as a field or
///     pass around as a constant. Pair it with a <see cref="Motion" />, which holds the state.
///     <para>
///         Springs are parameterised the way a designer thinks — a <em>response</em> (roughly the
///         settle period, in seconds) and a <em>damping</em> ratio (1 = no overshoot, below 1 =
///         bouncy) — rather than raw stiffness and damping coefficients. The usual constants fall
///         out of those: ω = 2π/response, stiffness = ω², damping coefficient = 2ζω.
///     </para>
/// </summary>
public readonly record struct MotionSpec
{
    /// <summary>
    ///     Distance at which a motion counts as arrived. In the value's own units, so a 0→1
    ///     progress animation wants a small number and a pixel offset wants something near half a
    ///     pixel. A motion also has to be slow enough to stop: it must move less than this in a
    ///     60 Hz frame.
    /// </summary>
    public float Epsilon { get; init; } = 0.001f;

    public MotionKind Kind { get; init; } = MotionKind.Duration;

    /// <summary>Seconds, <see cref="MotionKind.Duration" /> only.</summary>
    public float Seconds { get; init; } = 0.25f;

    /// <summary>Easing, <see cref="MotionKind.Duration" /> only. Null means linear.</summary>
    public Func<float, float>? Curve { get; init; }

    /// <summary>Approximate settle period in seconds, <see cref="MotionKind.Spring" /> only.</summary>
    public float Response { get; init; } = 0.35f;

    /// <summary>Damping ratio: 1 critically damped, below 1 overshoots, above 1 crawls in.</summary>
    public float Damping { get; init; } = 1f;

    /// <summary>Velocity decay per second, <see cref="MotionKind.Decay" /> only.</summary>
    public float Friction { get; init; } = 3.2f;

    public MotionSpec() { }

    public static MotionSpec Duration(float seconds, Func<float, float>? curve = null) =>
        new() {
            Kind = MotionKind.Duration,
            Seconds = seconds,
            Curve = curve,
        };

    public static MotionSpec Spring(float response = 0.35f, float damping = 1f) =>
        new() {
            Kind = MotionKind.Spring,
            Response = response,
            Damping = damping,
        };

    public static MotionSpec Decay(float friction = 3.2f) =>
        new() {
            Kind = MotionKind.Decay,
            Friction = friction,
        };

    /// <summary>Critically damped, no overshoot — the default for anything that must not wobble.</summary>
    public static MotionSpec Smooth => Spring(response: 0.35f, damping: 1f);

    /// <summary>Quick with a hint of overshoot — buttons, sheets, anything under a finger.</summary>
    public static MotionSpec Snappy => Spring(response: 0.22f, damping: 0.85f);

    /// <summary>Visibly springy. Use sparingly and never on something the user is reading.</summary>
    public static MotionSpec Bouncy => Spring(response: 0.45f, damping: 0.62f);
}

/// <summary>
///     A one-dimensional value in motion: where it is, how fast it is going, and where it is headed.
///     <see cref="Tick" /> advances it and reports whether it is still moving; <see cref="Retarget" />
///     aims it somewhere new <em>without</em> throwing away the velocity, which is the whole reason
///     this type exists — a spring interrupted mid-flight bends toward the new target instead of
///     restarting from a standstill.
///     <para>
///         A struct with no allocation anywhere in <see cref="Tick" />: hold one per animated value
///         as a field. Springs integrate at a fixed internal substep so a given spec settles the
///         same way at 30, 60 and 144 Hz — and so a motion on the fixed-step gameplay clock stays
///         deterministic.
///     </para>
/// </summary>
public struct Motion
{
    /// <summary>Internal integration step. Small enough that a stiff spring stays stable.</summary>
    private const float Substep = 1f / 240f;

    private float _carry; // unconsumed time below one substep
    private float _elapsed; // Duration kind: seconds into the run
    private float _start; // Duration kind: where this run began

    public float Value;
    public float Velocity;
    public float Target;
    public MotionSpec Spec;

    public Motion(MotionSpec spec, float value = 0f, float target = 0f, float velocity = 0f)
    {
        Spec = spec;
        Value = value;
        Target = target;
        Velocity = velocity;
        _start = value;
    }

    /// <summary>True once the value has arrived and stopped (a decay: once it has stopped).</summary>
    public readonly bool IsSettled
    {
        get
        {
            if (Spec.Kind == MotionKind.Duration) return _elapsed >= Spec.Seconds;
            bool slow = MathF.Abs(Velocity) < Spec.Epsilon * 60f;
            return Spec.Kind == MotionKind.Decay
                ? slow
                : slow && MathF.Abs(Target - Value) < Spec.Epsilon;
        }
    }

    /// <summary>
    ///     Where a <see cref="MotionKind.Decay" /> is heading if nothing interrupts it — what a fling
    ///     needs in order to pick the page or snap point it is aiming at, on the frame it is
    ///     released. A prediction, not a promise: the motion stops once it is crawling rather than
    ///     asymptotically, so it comes to rest slightly short of this.
    /// </summary>
    public readonly float Projected => Spec.Kind == MotionKind.Decay && Spec.Friction > 0f
        ? Value + (Velocity / Spec.Friction)
        : Target;

    /// <summary>
    ///     Aim at a new target, keeping the current velocity. For a spring or a decay that is all it
    ///     takes. A <see cref="MotionKind.Duration" /> motion has no way to honour an incoming
    ///     velocity — its curve dictates the whole shape — so it restarts from the current value;
    ///     that discontinuity is exactly what a spring spec buys you out of.
    /// </summary>
    public void Retarget(float target)
    {
        Target = target;
        if (Spec.Kind != MotionKind.Duration) return;
        _start = Value;
        _elapsed = 0f;
    }

    /// <summary>Jump to a value and stop dead — no callbacks, no easing.</summary>
    public void SnapTo(float value)
    {
        Value = Target = _start = value;
        Velocity = 0f;
        _elapsed = Spec.Seconds;
        _carry = 0f;
    }

    /// <summary>Restart a <see cref="MotionKind.Duration" /> run from the current value.</summary>
    public void Restart()
    {
        _start = Value;
        _elapsed = 0f;
    }

    /// <summary>
    ///     Advance by <paramref name="dt" /> seconds. Returns true while the motion is still moving —
    ///     a caller driving a ticker must stop it when this returns false, or the frame loop never
    ///     goes back to sleep.
    /// </summary>
    public bool Tick(float dt)
    {
        if (dt <= 0f) return !IsSettled;

        switch (Spec.Kind)
        {
            case MotionKind.Duration:
            {
                float duration = MathF.Max(x: Spec.Seconds, y: 1e-6f);
                _elapsed = MathF.Min(x: _elapsed + dt, y: duration);
                float t = _elapsed / duration;
                float previous = Value;
                Value = _start + ((Target - _start) * (Spec.Curve?.Invoke(t) ?? t));
                // Reported so an interrupted duration animation can hand a sane velocity to a
                // spring that takes over — the seam that makes mixed retargeting look continuous.
                Velocity = (Value - previous) / dt;
                if (_elapsed < duration) return true;
                Value = Target;
                Velocity = 0f;
                return false;
            }

            case MotionKind.Decay:
            {
                float friction = MathF.Max(x: Spec.Friction, y: 1e-4f);
                // Closed form, so a long dt is as accurate as many short ones:
                // v(t) = v·e^(−ft), x(t) = x + v·(1 − e^(−ft))/f.
                float decay = MathF.Exp(-friction * dt);
                Value += Velocity * (1f - decay) / friction;
                Velocity *= decay;
                if (!IsSettled) return true;
                Velocity = 0f;
                Target = Value;
                return false;
            }

            default:
            {
                // Semi-implicit Euler at a fixed substep: stable for stiff springs, and identical
                // at any frame rate. Time below one substep carries to the next tick rather than
                // being dropped (which would make a spring run slow at high frame rates).
                float omega = MathF.Tau / MathF.Max(x: Spec.Response, y: 1e-3f);
                float stiffness = omega * omega;
                float damping = 2f * Spec.Damping * omega;

                _carry += dt;
                while (_carry >= Substep)
                {
                    _carry -= Substep;
                    float accel = (-stiffness * (Value - Target)) - (damping * Velocity);
                    Velocity += accel * Substep;
                    Value += Velocity * Substep;
                }

                if (!IsSettled) return true;
                Value = Target;
                Velocity = 0f;
                _carry = 0f;
                return false;
            }
        }
    }
}
