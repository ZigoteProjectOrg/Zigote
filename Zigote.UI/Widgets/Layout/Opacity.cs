using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Applies a constant opacity in [0,1] to its child via the paint alpha stack. For an animated
///     fade, prefer <c>AnimatedOpacity</c> or <c>FadeTransition</c>.
/// </summary>
public class Opacity : Widget
{
    private Size _size;

    /// <summary>
    ///     Applies a constant opacity in [0,1] to its child, e.g.
    ///     <c>new Opacity(opacity: 0.5, child: w)</c>.
    /// </summary>
    public Opacity(double opacity, Widget? child = null)
    {
        Value = (float)opacity;
        Child = child;
    }

    public float Value { get; set; }
    public Widget? Child { get; set; }

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
        var a = Math.Clamp(Value, 0f, 1f);
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
        if (Value <= 0.001f) return null;
        return Bounds.Contains(point.X, point.Y) ? Child?.HitTest(point) : null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}