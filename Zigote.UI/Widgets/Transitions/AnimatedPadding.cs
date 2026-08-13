using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Smoothly animates the inset around its child when <see cref="Insets" /> changes — useful for
///     expand-on-hover and selection affordances.
/// </summary>
public sealed class AnimatedPadding : ImplicitlyAnimatedWidget
{
    private EdgeInsets _from;
    private Size _size;
    private EdgeInsets _to;

    public AnimatedPadding(EdgeInsets insets, Widget? child = null, float duration = 0.25f,
        Func<float, float>? curve = null) : base(duration, curve)
    {
        _from = _to = insets;
        Child = child;
    }

    public Widget? Child { get; set; }

    public EdgeInsets Insets
    {
        get => _to;
        set
        {
            if (value == _to) return;
            _from = Current;
            _to = value;
            Animate();
        }
    }

    private EdgeInsets Current
    {
        get
        {
            var t = Progress;
            return new EdgeInsets(
                _from.Left + (_to.Left - _from.Left) * t,
                _from.Top + (_to.Top - _from.Top) * t,
                _from.Right + (_to.Right - _from.Right) * t,
                _from.Bottom + (_to.Bottom - _from.Bottom) * t
            );
        }
    }

    public override Size Measure(Constraints c)
    {
        var insets = Current;
        var childSize = Child?.Measure(c.Deflate(insets)) ?? Size.Zero;
        _size = c.Constrain(
            new Size(
                childSize.Width + insets.Horizontal,
                childSize.Height + insets.Vertical
            )
        );
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
        var insets = Current;
        Child?.Layout(new Offset(origin.X + insets.Left, origin.Y + insets.Top));
    }

    public override void Paint(PaintList paint)
    {
        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? Child?.HitTest(point) : null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}
