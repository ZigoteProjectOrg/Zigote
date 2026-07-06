using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Clips its child to its own bounds (an axis-aligned rectangle) using the paint clip stack.
///     Useful for masking overflowing content.
/// </summary>
public class ClipRect(Widget? child = null) : Widget
{
    private Size _size;

    public Widget? Child { get; set; } = child;

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
        if (Child is null) return;
        paint.AddClipStart(Bounds);
        Child.Paint(paint);
        paint.AddClipEnd();
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

/// <summary>
///     Clips its child to a rounded rectangle. The corners are masked on the GPU (an SDF coverage
///     multiply in the shape/text/image pipelines), so any child content — fills, text, images —
///     is cleanly rounded off. Liquid-glass and custom shader-effect children fall back to the
///     bounding-rect scissor.
/// </summary>
public class ClipRRect(float radius, Widget? child = null) : Widget
{
    private Size _size;

    public float Radius { get; set; } = radius;
    public Widget? Child { get; set; } = child;

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
        if (Child is null) return;
        paint.AddClipStart(Bounds, Radius);
        Child.Paint(paint);
        paint.AddClipEnd();
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