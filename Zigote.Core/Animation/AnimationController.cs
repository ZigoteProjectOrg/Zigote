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
    private bool _repeats;
    private bool _reverseRepeat;
    private Ticker? _ticker;

    /// <param name="durationSeconds">Animation duration.</param>
    /// <param name="vsync">
    ///     Optional ticker provider (usually the enclosing <c>WidgetState</c>).
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

    public float Duration { get; set; }
    public float Progress { get; private set; }
    public AnimationStatus Status { get; private set; } = AnimationStatus.Idle;
    public Func<float, float> Curve { get; set; } = Curves.EaseInOut;

    /// <summary>Current eased value in [0, 1].</summary>
    public float Value => Curve(Progress);

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
        Status = AnimationStatus.Forward;
        _ticker?.Start();
    }

    /// <summary>Start animating in reverse (1 → 0).</summary>
    public void Reverse()
    {
        _repeats = false;
        _direction = -1f;
        if (Progress <= 0f) Progress = 1f;
        Status = AnimationStatus.Reverse;
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
        _ticker?.Start();
    }

    /// <summary>Jump to end without animating.</summary>
    public void Complete()
    {
        _repeats = false;
        Progress = 1f;
        Status = AnimationStatus.Completed;
        _ticker?.Stop();
    }

    /// <summary>Jump to start without animating.</summary>
    public void Dismiss()
    {
        _repeats = false;
        Progress = 0f;
        Status = AnimationStatus.Dismissed;
        _ticker?.Stop();
    }

    /// <summary>Advance the animation by <paramref name="dt" /> seconds. Call once per frame.</summary>
    public void Tick(float dt)
    {
        if (Status is not (AnimationStatus.Forward or AnimationStatus.Reverse)) return;

        // Delta-time driven so a given animation runs at the same wall-clock speed on any frame rate;
        // clamped to MaxFrameDelta so a stalled frame can't jump/finish it (see MaxFrameDelta).
        dt = Math.Clamp(dt, 0f, MaxFrameDelta);

        var step = (Duration > 0f ? dt / Duration : 1f) * _direction;
        Progress = Math.Clamp(Progress + step, 0f, 1f);

        if (_repeats)
        {
            if (Progress >= 1f)
            {
                if (_reverseRepeat) _direction = -1f;
                else Progress = 0f;
            }
            else if (Progress <= 0f && _direction < 0f)
            {
                _direction = 1f;
            }

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

    public static implicit operator float(AnimationController c)
    {
        return c.Value;
    }
}