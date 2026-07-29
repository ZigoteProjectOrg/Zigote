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
        // The parent's ceiling wins over an imposed minimum. Constraints raises max to min when the
        // two cross (Constraints ctor), so a 280-wide minimum inside a 264-wide parent would measure
        // 280 and paint 16 px outside it — the failure mode of every fixed-min box on a phone.
        var maxW = Math.Min(c.MaxWidth, Constraints.MaxWidth);
        var maxH = Math.Min(c.MaxHeight, Constraints.MaxHeight);
        var merged = new Constraints(
            Math.Min(Math.Max(c.MinWidth, Constraints.MinWidth), maxW),
            maxW,
            Math.Min(Math.Max(c.MinHeight, Constraints.MinHeight), maxH),
            maxH
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