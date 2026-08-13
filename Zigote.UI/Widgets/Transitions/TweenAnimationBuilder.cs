using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Drives a <see cref="Tween{T}" /> from its begin to its end over a duration, rebuilding a child
///     from the current value via <see cref="Builder" />. Setting <see cref="End" /> re-aims the tween
///     from the current value — the implicit, builder-based animation primitive.
/// </summary>
public sealed class TweenAnimationBuilder<T> : ImplicitlyAnimatedWidget
{
    private readonly Tween<T> _tween;
    private Widget? _built;
    private float _lastProgress = float.NaN;
    private Size _size;

    public TweenAnimationBuilder(Tween<T> tween, Func<T, Widget> builder,
        float duration = 0.25f, Func<float, float>? curve = null) : base(duration, curve)
    {
        _tween = tween;
        Builder = builder;
        Animate();
    }

    public Func<T, Widget> Builder { get; set; }

    /// <summary>The current target. Reassigning animates from the current value to the new one.</summary>
    public T End
    {
        get => _tween.End;
        set
        {
            if (EqualityComparer<T>.Default.Equals(value, _tween.End)) return;
            _tween.Begin = _tween.Evaluate(Progress);
            _tween.End = value;
            Animate();
        }
    }

    private void RebuildIfNeeded()
    {
        var p = Progress;
        if (!float.IsNaN(_lastProgress) && MathF.Abs(p - _lastProgress) < 0.001f &&
            _built is not null) return;
        _lastProgress = p;
        // Attach-then-detach (Widget.SwapChild), and it matters most here: this re-runs on every
        // animation frame, so a builder that wraps a retained child used to unmount and remount that
        // child ~60×/second — disposing and re-creating everything it owns each time.
        var previous = _built;
        _built = Builder(_tween.Evaluate(p));
        SwapChild(previous, _built);
    }

    public override Size Measure(Constraints c)
    {
        RebuildIfNeeded();
        _size = _built?.Measure(c) ?? Size.Zero;
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _built?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _built?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _built?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _built is not null ? [_built] : [];
    }
}
