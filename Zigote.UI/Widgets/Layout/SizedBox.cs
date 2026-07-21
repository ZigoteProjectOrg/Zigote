using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>Forces child into a fixed size (or expands to fill if Width/Height are null).</summary>
public class SizedBox(float? width = null, float? height = null, Widget? child = null)
    : Widget
{
    private Size _size;

    public float? Width { get; set; } = width;
    public float? Height { get; set; } = height;
    public Widget? Child { get; set; } = child;

    public static SizedBox Shrink()
    {
        return new SizedBox(0f, 0f);
    }

    public static SizedBox Square(float size, Widget? child = null)
    {
        return new SizedBox(size, size, child);
    }

    public static SizedBox Expand(Widget? child = null)
    {
        return new SizedBox(float.PositiveInfinity, float.PositiveInfinity, child);
    }

    public override Size Measure(Constraints c)
    {
        // Clamp explicit dimensions to the parent's allowed range.
        // This prevents SizedBox from overflowing its parent when the explicit size
        // is larger than the available space (e.g. inside a tight canvas preview).
        float? tw = Width.HasValue ? Math.Clamp(Width.Value, c.MinWidth, c.MaxWidth) : null;
        float? th = Height.HasValue ? Math.Clamp(Height.Value, c.MinHeight, c.MaxHeight) : null;

        if (Child == null)
        {
            _size = new Size(tw ?? c.MinWidth, th ?? c.MinHeight);
            return _size;
        }

        var childC = new Constraints(
            tw ?? c.MinWidth,
            tw ?? c.MaxWidth,
            th ?? c.MinHeight,
            th ?? c.MaxHeight
        );
        var childSz = Child.Measure(childC);

        _size = new Size(tw ?? childSz.Width, th ?? childSz.Height);
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
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child?.HitTest(point) ?? null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}