using Zigote.Core;
using Zigote.Core.Paint;
using AlignT = Zigote.UI.Widgets.Layout.Alignment;

namespace Zigote.UI.Widgets.Transitions;

/// <summary>
///     Smoothly moves its child to a new <see cref="AlignT" /> when it changes — e.g. sliding a
///     thumb or floating button between corners.
/// </summary>
public sealed class AnimatedAlign : ImplicitlyAnimatedWidget
{
    private Size _childSize;
    private AlignT _from;
    private Size _size;
    private AlignT _to;

    public AnimatedAlign(AlignT alignment, Widget? child = null, float duration = 0.25f,
        Func<float, float>? curve = null) : base(duration, curve)
    {
        _from = _to = alignment;
        Child = child;
    }

    public Widget? Child { get; set; }

    public AlignT Alignment
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

    private AlignT Current => AlignT.Lerp(_from, _to, Progress);

    public override Size Measure(Constraints c)
    {
        _childSize = Child?.Measure(
            new Constraints(
                0,
                c.MaxWidth,
                0,
                c.MaxHeight
            )
        ) ?? Size.Zero;
        _size = c.Constrain(
            new Size(
                float.IsFinite(c.MaxWidth) ? c.MaxWidth : _childSize.Width,
                float.IsFinite(c.MaxHeight) ? c.MaxHeight : _childSize.Height
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
        var o = Current.Within(_size, _childSize);
        Child?.Layout(new Offset(origin.X + o.X, origin.Y + o.Y));
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
