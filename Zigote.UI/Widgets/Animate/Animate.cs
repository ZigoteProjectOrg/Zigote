using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets;

/// <summary>
///     A flutter_animate-style declarative animation wrapper. Wrap any widget and chain effects:
///     <code>
///     new Label("Hello").Animate()
///         .Fade(duration: 500.ms)
///         .Scale(delay: 500.ms);   // runs after the fade
///     </code>
///     <para>
///         Effects run in parallel by default. Each effect's <c>delay</c>/<c>duration</c>/<c>curve</c>
///         inherits from the previous effect when omitted (the first inherits
///         <see cref="DefaultDuration" />/<see cref="DefaultCurve" />); a <see cref="Then" /> resets
///         the
///         baseline so following effects start where the previous one ended.
///     </para>
///     <para>
///         For state-driven transitions set <see cref="Target" /> to 0 or 1: the animation plays
///         forward/reverse toward it, snapping without
///         animating on first mount. Otherwise it plays once on mount when <see cref="AutoPlay" /> is
///         set.
///     </para>
///     <para>
///         Backed by the paint pipeline's real capabilities: <see cref="FadeEffect" /> (alpha),
///         <see cref="MoveEffect" />/<see cref="SlideEffect" />/<see cref="ShakeEffect" />
///         (translation)
///         are pure paint; <see cref="ScaleEffect" /> scales via layout (re-measures the child and
///         centres it, so the surrounding slot stays stable). Rotation/blur/colour-matrix effects are
///         intentionally absent — the renderer has no such primitives.
///     </para>
/// </summary>
public sealed class Animate : Widget
{
    private readonly List<AnimateEffect> _effects = [];
    private float _alpha = 1f;
    private bool _explicitTarget;
    private bool _mounted;

    private Size _natural;
    private bool _played;
    private int _resolvedCount = -1;
    private float _scale = 1f;
    private float? _target;

    private float _totalS = 0.0001f;
    private float _tx;
    private float _ty;

    public Animate(Widget? child = null)
    {
        Child = child;
        Controller =
            new AnimationController(
                durationSeconds: 0.0001f,
                vsync: this
            ) { Curve = Curves.Linear };
        Controller.OnTick += MarkNeedsLayout;
        Controller.OnCompleted += () => OnComplete?.Invoke(Controller);
    }

    /// <summary>
    ///     Duration used by the first effect (and any effect that omits its own). 300 ms like
    ///     flutter_animate.
    /// </summary>
    public static TimeSpan DefaultDuration { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Curve used by the first effect (and any effect that omits its own).</summary>
    public static Func<float, float> DefaultCurve { get; set; } = Curves.EaseOut;

    public Widget? Child { get; set; }

    /// <summary>Play the timeline once when first mounted. Ignored when <see cref="Target" /> is set.</summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>Fires once when a forward play reaches the end.</summary>
    public Action<AnimationController>? OnComplete { get; set; }

    /// <summary>
    ///     Fires when the timeline starts playing (mount, <see cref="Play" />, or a forward
    ///     <see cref="Target" />).
    /// </summary>
    public Action<AnimationController>? OnPlay { get; set; }

    /// <summary>The controller driving the timeline — for advanced use (repeat, manual value, listeners).</summary>
    public AnimationController Controller { get; }

    /// <summary>
    ///     Drive the animation toward a target position (0 = start, 1 = end), reversing when it drops.
    ///     Set this instead of <see cref="AutoPlay" /> for state-driven transitions. Snaps (no animation)
    ///     on first mount; animates on every change after.
    /// </summary>
    public float? Target
    {
        get => _target;
        set
        {
            _explicitTarget = value.HasValue;
            if (Nearly(a: _target, b: value)) return;
            _target = value;
            if (_mounted && value is { } t) DriveToTarget(t: t, animate: true);
        }
    }

    // ── Fluent effect builders ─────────────────────────────────────────────────

    /// <summary>Append a raw effect (escape hatch / custom effects).</summary>
    public Animate Effect(AnimateEffect effect)
    {
        _effects.Add(effect);
        _resolvedCount = -1;
        return this;
    }

    /// <summary>
    ///     Fade opacity. Defaults to 0→1 (fade in); pass only <paramref name="begin" /> or
    ///     <paramref name="end" /> for smart defaults.
    /// </summary>
    public Animate Fade(TimeSpan? duration = null, TimeSpan? delay = null,
        Func<float, float>? curve = null, float? begin = null, float? end = null)
    {
        return Effect(
            new FadeEffect {
                Duration = duration,
                Delay = delay,
                Curve = curve,
                Begin = begin,
                End = end,
            }
        );
    }

    /// <summary>Fade in (0→1).</summary>
    public Animate FadeIn(TimeSpan? duration = null, TimeSpan? delay = null,
        Func<float, float>? curve = null)
    {
        return Fade(
            duration: duration,
            delay: delay,
            curve: curve,
            begin: 0f,
            end: 1f
        );
    }

    /// <summary>Fade out (1→0).</summary>
    public Animate FadeOut(TimeSpan? duration = null, TimeSpan? delay = null,
        Func<float, float>? curve = null)
    {
        return Fade(
            duration: duration,
            delay: delay,
            curve: curve,
            begin: 1f,
            end: 0f
        );
    }

    /// <summary>Uniform scale about the centre. Defaults to 0→1 (scale up).</summary>
    public Animate Scale(TimeSpan? duration = null, TimeSpan? delay = null,
        Func<float, float>? curve = null, float? begin = null, float? end = null)
    {
        return Effect(
            new ScaleEffect {
                Duration = duration,
                Delay = delay,
                Curve = curve,
                Begin = begin,
                End = end,
            }
        );
    }

    /// <summary>Translate by a pixel offset. Defaults to (0,24)→(0,0) (rise into place).</summary>
    public Animate Move(TimeSpan? duration = null, TimeSpan? delay = null,
        Func<float, float>? curve = null, Offset? begin = null, Offset? end = null)
    {
        return Effect(
            new MoveEffect {
                Duration = duration,
                Delay = delay,
                Curve = curve,
                Begin = begin,
                End = end,
            }
        );
    }

    /// <summary>Translate by a fraction of the widget's own size. Defaults to (0,-0.25)→(0,0).</summary>
    public Animate Slide(TimeSpan? duration = null, TimeSpan? delay = null,
        Func<float, float>? curve = null, Offset? begin = null, Offset? end = null)
    {
        return Effect(
            new SlideEffect {
                Duration = duration,
                Delay = delay,
                Curve = curve,
                Begin = begin,
                End = end,
            }
        );
    }

    /// <summary>Oscillating positional shake (attention-getter).</summary>
    public Animate Shake(TimeSpan? duration = null, TimeSpan? delay = null,
        float hz = 8f, Offset? amount = null)
    {
        return Effect(
            new ShakeEffect {
                Duration = duration,
                Delay = delay,
                Hz = hz,
                Amount = amount ?? new Offset(x: 6f, y: 0f),
            }
        );
    }

    /// <summary>Reset the timing baseline so subsequent effects start where the previous one ended.</summary>
    public Animate Then(TimeSpan? delay = null, TimeSpan? duration = null,
        Func<float, float>? curve = null)
    {
        return Effect(
            new ThenEffect {
                Delay = delay,
                Duration = duration,
                Curve = curve,
            }
        );
    }

    // ── Playback control ───────────────────────────────────────────────────────

    /// <summary>Play the timeline forward from the current position.</summary>
    public void Play()
    {
        EnsureResolved();
        Controller.Forward();
        OnPlay?.Invoke(Controller);
    }

    /// <summary>Restart the timeline from the beginning.</summary>
    public void Restart()
    {
        EnsureResolved();
        Controller.Dismiss();
        Controller.Forward();
        OnPlay?.Invoke(Controller);
    }

    /// <summary>Play the timeline in reverse from the current position.</summary>
    public void Reverse()
    {
        EnsureResolved();
        Controller.Reverse();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    protected override void OnMount()
    {
        // The previous ticker went with the last unmount; rebind so the timeline resumes after a
        // detach→re-attach cycle. CreateTicker owns it, so nothing to dispose by hand.
        Controller.AttachTicker(this);
        EnsureResolved();

        if (!_mounted)
        {
            _mounted = true;
            if (_explicitTarget)
                DriveToTarget(t: _target ?? 0f, animate: false); // snap to initial state
            else if (AutoPlay && !_played)
            {
                _played = true;
                Controller.Forward();
                OnPlay?.Invoke(Controller);
            }
        }
    }

    // ── Timeline resolution + per-frame fold ─────────────────────────────────────

    private void EnsureResolved()
    {
        if (_resolvedCount == _effects.Count) return;
        _resolvedCount = _effects.Count;

        float defaultDur = (float)DefaultDuration.TotalSeconds;
        float priorBeginS = 0f, priorEndS = 0f, priorDurS = defaultDur;
        var priorCurve = DefaultCurve;
        bool hasPrior = false;
        float total = 0f;

        foreach (var e in _effects)
        {
            if (e.IsMarker)
            {
                float markerBegin = (hasPrior ? priorEndS : 0f) +
                                    (float)(e.Delay?.TotalSeconds ?? 0d);
                e.BeginS = markerBegin;
                e.EndS = markerBegin;
                e.ResolvedCurve = priorCurve;
                // Shift the baseline but keep prior duration/curve so the next effect inherits the
                // real values from before the Then().
                priorBeginS = markerBegin;
                priorEndS = markerBegin;
                hasPrior = true;
                continue;
            }

            float durS = (float)(e.Duration?.TotalSeconds ?? (hasPrior ? priorDurS : defaultDur));
            float beginS = e.Delay is { } d
                ? (float)d.TotalSeconds
                : hasPrior
                    ? priorBeginS
                    : 0f;
            var curve = e.Curve ?? (hasPrior ? priorCurve : DefaultCurve);

            e.BeginS = beginS;
            e.EndS = beginS + durS;
            e.ResolvedCurve = curve;

            total = MathF.Max(x: total, y: e.EndS);
            priorBeginS = beginS;
            priorEndS = e.EndS;
            priorDurS = durS;
            priorCurve = curve;
            hasPrior = true;
        }

        _totalS = MathF.Max(x: total, y: 0.0001f);
        Controller.Duration = _totalS;
    }

    private void Recompute()
    {
        EnsureResolved();
        var f = AnimateFrame.Identity;
        if (_effects.Count > 0)
        {
            float elapsed =
                Controller.Value * _totalS; // Value == Progress (Linear controller curve)
            foreach (var e in _effects)
            {
                if (e.IsMarker) continue;
                float durS = MathF.Max(x: 0.0001f, y: e.EndS - e.BeginS);
                float raw = Math.Clamp(value: (elapsed - e.BeginS) / durS, min: 0f, max: 1f);
                float eased = e.ResolvedCurve(raw);
                e.Apply(
                    frame: ref f,
                    raw: raw,
                    eased: eased,
                    natural: _natural
                );
            }
        }

        _alpha = Math.Clamp(value: f.Alpha, min: 0f, max: 1f);
        _scale = MathF.Max(x: 0f, y: f.Scale);
        _tx = f.Tx;
        _ty = f.Ty;
    }

    private void DriveToTarget(float t, bool animate)
    {
        EnsureResolved();
        if (t >= 0.5f)
        {
            if (animate) Controller.Forward();
            else Controller.Complete();
            if (animate) OnPlay?.Invoke(Controller);
        }
        else
        {
            if (animate) Controller.Reverse();
            else Controller.Dismiss();
        }
    }

    private static bool Nearly(float? a, float? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return MathF.Abs(a.Value - b.Value) < 1e-4f;
    }

    // ── Measure / Layout / Paint ─────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _natural = Child?.Measure(c) ?? Size.Zero;
        Recompute();
        return
            _natural; // stable slot — scale re-measures the child in Layout without resizing the slot
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _natural.Width,
            height: _natural.Height
        );
        if (Child is null) return;

        if (_scale is > 0.999f and < 1.001f)
        {
            Child.Layout(origin);
            return;
        }

        float sw = _natural.Width * _scale;
        float sh = _natural.Height * _scale;
        Child.Measure(Constraints.Tight(width: sw, height: sh));
        Child.Layout(
            new Offset(
                x: origin.X + ((_natural.Width - sw) / 2f),
                y: origin.Y + ((_natural.Height - sh) / 2f)
            )
        );
    }

    public override void Paint(PaintList paint)
    {
        if (Child is null || _alpha <= 0.001f) return;

        bool fade = _alpha < 0.999f;
        bool move = _tx != 0f || _ty != 0f;
        if (fade) paint.PushAlpha(_alpha);
        if (move) paint.PushTranslate(dx: _tx, dy: _ty);
        Child.Paint(paint);
        if (move) paint.PopTranslate();
        if (fade) paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        if (_alpha <= 0.01f || Child is null) return null;
        var p = _tx != 0f || _ty != 0f ? new Offset(x: point.X - _tx, y: point.Y - _ty) : point;
        return Child.HitTest(p);
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
