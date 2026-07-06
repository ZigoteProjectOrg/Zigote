using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Smoothly fades its child when <see cref="Opacity" /> changes. The implicit counterpart to
///     <see cref="FadeTransition" /> (no controller to manage).
/// </summary>
public sealed class AnimatedOpacity : ImplicitlyAnimatedWidget
{
    private float _from;
    private Size _size;
    private float _to;

    public AnimatedOpacity(float opacity, Widget? child = null, float duration = 0.25f,
        Func<float, float>? curve = null)
        : base(duration, curve)
    {
        _from = _to = Math.Clamp(opacity, 0f, 1f);
        Child = child;
    }

    public Widget? Child { get; set; }

    public float Opacity
    {
        get => _to;
        set
        {
            var v = Math.Clamp(value, 0f, 1f);
            if (MathF.Abs(v - _to) < 1e-4f) return;
            _from = Current;
            _to = v;
            Animate();
        }
    }

    private float Current => _from + (_to - _from) * Progress;

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? Size.Zero;
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
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        var a = Current;
        if (a <= 0.001f) return;
        if (a >= 0.999f)
        {
            Child?.Paint(paint);
            return;
        }

        paint.PushAlpha(a);
        Child?.Paint(paint);
        paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        if (Current <= 0.01f) return null;
        return Bounds.Contains(point.X, point.Y) ? Child?.HitTest(point) : null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}