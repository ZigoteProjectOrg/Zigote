using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>Fills its constrained area with a solid color. Passes constraints to an optional child.</summary>
public class ColoredBox(Color color, Widget? child = null) : Widget
{
    private Size _size;

    public Color Color { get; set; } = color;
    public Widget? Child { get; set; } = child;
    public float Radius { get; set; } = 0f;

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? c.Constrain(Size.Zero);
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

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: Color, radius: Radius);
        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
