namespace Zigote.Core.Animation;

public enum AnimationStatus
{
    Idle,
    Forward,
    Reverse,
    Completed,
    Dismissed,
}

/// <summary>
///     Drives progress from 0→1 (forward) or 1→0 (reverse) over a given duration.
///     Call <see cref="Tick" /> each frame with the delta-time in seconds.
/// </summary>
public sealed class AnimationController
{
    private float _direction = 1f;
    private Motion _motion;
    private bool _repeats;
    private bool _reverseRepeat;
    private Ticker? _ticker;

    /// <param name="durationSeconds">Animation duration.</param>
    /// <param name="vsync">
    ///     Optional ticker provider (usually the enclosing widget — every <c>Widget</c> is one).
    ///     When supplied the controller ticks automatically each frame without
    ///     any manual <see cref="Tick" /> calls. When null, call <see cref="Tick" /> yourself.
    /// </param>
    public AnimationController(float durationSeconds = 0.25f, ITickerProvider? vsync = null)
    {
        Duration = durationSeconds;
        if (vsync != null)
            _ticker = vsync.CreateTicker(Tick);
    }

    public static Action? RequestFrameAction { get; set; }

    /// <summary>
    ///     Upper bound (seconds) on the per-frame delta any animation will advance by in a single
    ///     <see cref="Tick" />. Animations are delta-time driven so they run at the same wall-clock speed
    ///     on any frame rate (a 0.2 s fade takes 0.2 s at 30 fps or 144 fps). This cap additionally keeps
    ///     them consistent when a frame *stalls* — a GC pause, a load spike, a debugger break, or a
    ///     window returning from the background can produce a huge delta that would otherwise jump the
    ///     animation forward (or finish it in one frame). Clamping to 100 ms leaves normal frame rates
    ///     (down to ~10 fps) untouched while absorbing hitches, so motion looks the same on a fast and a
    ///     janky device.
    /// </summary>
    public static float MaxFrameDelta { get; set; } = 0.1f;

    /// <summary>
    ///     Global multiplier on every animation's dt. 1 is real time; 0.2 is the slow-motion a
    ///     DevTools slider binds to when someone is trying to see what a transition actually does.
    /// </summary>
    public static float TimeScale { get; set; } = 1f;

    /// <summary>
    ///     Honour the platform's "reduce motion" preference: <see cref="Forward" /> and
    ///     <see cref="Reverse" /> jump straight to their end state, still firing the
    ///     completed/dismissed edges so anything waiting on them proceeds. Set it once at startup
    ///     from the OS setting; widgets need no special case.
    ///     <para>
    ///         <see cref="Repeat" /> is deliberately exempt. A spinner or an indeterminate progress
    ///         bar is the only sign the app is still working, and freezing it reads as a hang.
    ///         Decorative loops opt out at their own level — <c>Animate.Loop</c> does.
    ///     </para>
    /// </summary>
    public static bool ReduceMotion { get; set; }

    public float Duration { get; set; }
    public float Progress { get; private set; }
    public AnimationStatus Status { get; private set; } = AnimationStatus.Idle;
    public Func<float, float> Curve { get; set; } = Curves.EaseInOut;

    /// <summary>
    ///     Rate multiplier for this controller alone. <c>AnimationPlayer</c> and
    ///     <c>SpriteAnimator</c> always had one; this is the widget side of the same knob.
    /// </summary>
    public float Speed { get; set; } = 1f;

    /// <summary>
    ///     Set a spring (or decay) spec to drive the progress physically instead of by
    ///     <see cref="Duration" /> and <see cref="Curve" />. The difference shows the moment an
    ///     animation is interrupted: a duration restarts from a standstill, a spring bends toward
    ///     the new target carrying the velocity it already had.
    ///     <para>
    ///         Null (the default) keeps the plain duration behaviour every existing caller has. A
    ///         bouncy spring overshoots, so <see cref="Progress" /> may leave [0,1] unless
    ///         <see cref="ClampProgress" /> is on.
    ///     </para>
    /// </summary>
    public MotionSpec? Spec { get; set; }

    /// <summary>Hold <see cref="Progress" /> inside [0,1], suppressing a bouncy spring's overshoot.</summary>
    public bool ClampProgress { get; set; } = true;

    /// <summary>
    ///     Progress units per second, signed. Non-zero only under a <see cref="Spec" />; it is what
    ///     a gesture hands over when it lets go, and what survives a retarget. Settable so a drag
    ///     can hand its release velocity to the spring that takes over.
    /// </summary>
    public float Velocity
    {
        get => _motion.Velocity;
        set => _motion.Velocity = value;
    }

    /// <summary>Current eased value in [0, 1] (a spring may overshoot — see <see cref="Spec" />).</summary>
    public float Value => Spec is { Kind: not MotionKind.Duration } ? Progress : Curve(Progress);

    /// <summary>
    ///     (Re)bind the driving ticker from <paramref name="vsync" />. A widget owning this controller
    ///     must call this from its <c>Attach</c> after a <c>Detach</c> disposed the previous ticker —
    ///     otherwise the controller keeps a reference to a disposed ticker whose <c>Start()</c> is a
    ///     no-op, and the animation stays frozen after any detach→re-attach cycle. Resumes ticking if a
    ///     transition was in flight.
    /// </summary>
    public void AttachTicker(ITickerProvider vsync)
    {
        _ticker = vsync.CreateTicker(Tick);
        if (Status is AnimationStatus.Forward or AnimationStatus.Reverse)
            _ticker.Start();
    }

    public event Action? OnCompleted;
    public event Action? OnDismissed;
    public event Action? OnTick;

    /// <summary>Start animating forward (0 → 1).</summary>
    public void Forward()
    {
        _repeats = false;
        _direction = 1f;
        if (Progress >= 1f) Progress = 0f;
        if (ReduceMotion)
        {
            Complete();
            OnCompleted?.Invoke();
            return;
        }

        Status = AnimationStatus.Forward;
        AimMotion();
        _ticker?.Start();
    }

    /// <summary>Start animating in reverse (1 → 0).</summary>
    public void Reverse()
    {
        _repeats = false;
        _direction = -1f;
        if (Progress <= 0f) Progress = 1f;
        if (ReduceMotion)
        {
            Dismiss();
            OnDismissed?.Invoke();
            return;
        }

        Status = AnimationStatus.Reverse;
        AimMotion();
        _ticker?.Start();
    }

    /// <summary>Toggle: forward if dismissed/idle, reverse if completed.</summary>
    public void Toggle()
    {
        if (Status is AnimationStatus.Completed or AnimationStatus.Forward)
            Reverse();
        else
            Forward();
    }

    /// <summary>
    ///     Loop the animation continuously. Call once to start looping.
    ///     When <paramref name="reverse" /> is true the animation ping-pongs (0→1→0→1…).
    ///     When false it wraps (0→1→0→1… restarting from 0 each cycle).
    /// </summary>
    public void Repeat(bool reverse = false)
    {
        _repeats = true;
        _reverseRepeat = reverse;
        _direction = 1f;
        Progress = 0f;
        Status = AnimationStatus.Forward;
        AimMotion();
        _ticker?.Start();
    }

    /// <summary>Jump to end without animating.</summary>
    public void Complete()
    {
        _repeats = false;
        Progress = 1f;
        _motion.SnapTo(1f);
        Status = AnimationStatus.Completed;
        _ticker?.Stop();
    }

    /// <summary>
    ///     Jump to an arbitrary progress without animating and without firing the completed/dismissed
    ///     edges — the seam for a gesture that drives the animation directly (a bottom sheet tracking
    ///     the finger). A following <see cref="Forward" />/<see cref="Reverse" /> resumes from here.
    /// </summary>
    public void Seek(float progress)
    {
        _repeats = false;
        Progress = Math.Clamp(value: progress, min: 0f, max: 1f);
        // A seek is a drag, so the spring keeps the velocity the finger gave it and only moves its
        // value — that is what lets a released sheet fling on from where it was let go.
        _motion.Value = Progress;
        Status = AnimationStatus.Idle;
        _ticker?.Stop();
        OnTick?.Invoke();
        RequestFrameAction?.Invoke();
    }

    /// <summary>Jump to start without animating.</summary>
    public void Dismiss()
    {
        _repeats = false;
        Progress = 0f;
        _motion.SnapTo(0f);
        Status = AnimationStatus.Dismissed;
        _ticker?.Stop();
    }

    /// <summary>Advance the animation by <paramref name="dt" /> seconds. Call once per frame.</summary>
    public void Tick(float dt)
    {
        if (Status is not (AnimationStatus.Forward or AnimationStatus.Reverse)) return;

        // Delta-time driven so a given animation runs at the same wall-clock speed on any frame rate;
        // clamped to MaxFrameDelta so a stalled frame can't jump/finish it (see MaxFrameDelta).
        // Speed and TimeScale scale what is left — the clamp is about frame hitches, not intent.
        dt = Math.Clamp(value: dt, min: 0f, max: MaxFrameDelta) * Speed * TimeScale;
        if (dt <= 0f) return;

        if (Spec is { Kind: not MotionKind.Duration })
        {
            TickSpring(dt);
            return;
        }

        float step = (Duration > 0f ? dt / Duration : 1f) * _direction;
        Progress = Math.Clamp(value: Progress + step, min: 0f, max: 1f);

        if (_repeats)
        {
            if (Progress >= 1f)
            {
                if (_reverseRepeat) _direction = -1f;
                else Progress = 0f;
            }
            else if (Progress <= 0f && _direction < 0f) _direction = 1f;

            Status = _direction > 0f ? AnimationStatus.Forward : AnimationStatus.Reverse;
        }
        else
        {
            if (Progress >= 1f)
            {
                Status = AnimationStatus.Completed;
                _ticker?.Stop();
                OnCompleted?.Invoke();
            }
            else if (Progress <= 0f)
            {
                Status = AnimationStatus.Dismissed;
                _ticker?.Stop();
                OnDismissed?.Invoke();
            }
        }

        OnTick?.Invoke();
        RequestFrameAction?.Invoke();
    }

    // Point the spring at whichever end the current direction is heading for, WITHOUT touching its
    // velocity — a Forward that interrupts a Reverse mid-flight bends the motion around instead of
    // restarting it from a standstill.
    private void AimMotion()
    {
        if (Spec is not { Kind: not MotionKind.Duration } spec) return;
        _motion.Spec = spec;
        _motion.Value = Progress;
        _motion.Retarget(_direction > 0f ? 1f : 0f);
    }

    private void TickSpring(float dt)
    {
        bool moving = _motion.Tick(dt);
        Progress = ClampProgress
            ? Math.Clamp(value: _motion.Value, min: 0f, max: 1f)
            : _motion.Value;

        if (!moving)
        {
            if (_repeats)
            {
                if (_reverseRepeat)
                    _direction = -_direction;
                else
                {
                    Progress = 0f;
                    _motion.SnapTo(0f);
                    _direction = 1f;
                }

                AimMotion();
                Status = _direction > 0f
                    ? AnimationStatus.Forward
                    : AnimationStatus.Reverse;
            }
            else if (_direction > 0f)
            {
                Status = AnimationStatus.Completed;
                _ticker?.Stop();
                OnCompleted?.Invoke();
            }
            else
            {
                Status = AnimationStatus.Dismissed;
                _ticker?.Stop();
                OnDismissed?.Invoke();
            }
        }

        OnTick?.Invoke();
        RequestFrameAction?.Invoke();
    }

    public static implicit operator float(AnimationController c) => c.Value;
}
