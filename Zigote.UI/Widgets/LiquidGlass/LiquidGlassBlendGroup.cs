using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.LiquidGlass;

/// <summary>
///     Groups multiple LiquidGlass shapes to blend them together seamlessly.
///     In forward rendering, it acts as a stacked layout container, preventing separate background
///     clipping.
/// </summary>
public class LiquidGlassBlendGroup : Widget
{
    private Size _size;

    public LiquidGlassBlendGroup(params Widget[] children) => Children.AddRange(children);

    public List<Widget> Children { get; } = [];

    public override Size Measure(Constraints c)
    {
        float maxW = 0f;
        float maxH = 0f;
        foreach (var child in Children)
        {
            var size = child.Measure(c);
            maxW = Math.Max(val1: maxW, val2: size.Width);
            maxH = Math.Max(val1: maxH, val2: size.Height);
        }

        _size = c.Constrain(new Size(width: maxW, height: maxH));
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
        foreach (var child in Children) child.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        foreach (var child in Children) child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;

        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTest(point);
            if (hit != null) return hit;
        }

        return this;
    }

    public override IEnumerable<Widget> GetChildren() => Children;
}
