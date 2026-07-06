using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Imposes additional min/max <see cref="Constraints" /> on its child, intersected with the
///     constraints handed down by the parent. Handy for enforcing a
///     minimum width or capping a maximum height independent of the surrounding layout.
/// </summary>
public class ConstrainedBox(Constraints constraints, Widget? child = null) : Widget
{
    private Size _size;

    public Constraints Constraints { get; set; } = constraints;
    public Widget? Child { get; set; } = child;

    public override Size Measure(Constraints c)
    {
        var merged = new Constraints(
            Math.Max(c.MinWidth, Constraints.MinWidth),
            Math.Min(c.MaxWidth, Constraints.MaxWidth),
            Math.Max(c.MinHeight, Constraints.MinHeight),
            Math.Min(c.MaxHeight, Constraints.MaxHeight)
        );

        _size = merged.Constrain(
            Child?.Measure(merged) ?? new Size(merged.MinWidth, merged.MinHeight)
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
        Child?.Layout(origin);
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