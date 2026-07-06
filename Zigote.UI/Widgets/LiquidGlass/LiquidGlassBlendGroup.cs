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

    public LiquidGlassBlendGroup(params Widget[] children)
    {
        Children.AddRange(children);
    }

    public List<Widget> Children { get; } = [];

    public override Size Measure(Constraints c)
    {
        var maxW = 0f;
        var maxH = 0f;
        foreach (var child in Children)
        {
            var size = child.Measure(c);
            maxW = Math.Max(maxW, size.Width);
            maxH = Math.Max(maxH, size.Height);
        }

        _size = c.Constrain(new Size(maxW, maxH));
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
        foreach (var child in Children) child.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        foreach (var child in Children) child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;

        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTest(point);
            if (hit != null) return hit;
        }

        return this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return Children;
    }
}