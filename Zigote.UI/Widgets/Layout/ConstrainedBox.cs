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
        float maxW = Math.Min(val1: c.MaxWidth, val2: Constraints.MaxWidth);
        float maxH = Math.Min(val1: c.MaxHeight, val2: Constraints.MaxHeight);
        var merged = new Constraints(
            minWidth: Math.Min(
                val1: Math.Max(val1: c.MinWidth, val2: Constraints.MinWidth),
                val2: maxW
            ),
            maxWidth: maxW,
            minHeight: Math.Min(
                val1: Math.Max(val1: c.MinHeight, val2: Constraints.MinHeight),
                val2: maxH
            ),
            maxHeight: maxH
        );

        _size = merged.Constrain(
            Child?.Measure(merged) ?? new Size(width: merged.MinWidth, height: merged.MinHeight)
        );
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint) => Child?.Paint(paint);

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? Child?.HitTest(point) : null;

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
