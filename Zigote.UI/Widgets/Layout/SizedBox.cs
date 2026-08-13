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

    public static SizedBox Shrink() => new(width: 0f, height: 0f);

    public static SizedBox Square(float size, Widget? child = null) =>
        new(width: size, height: size, child: child);

    public static SizedBox Expand(Widget? child = null) => new(
        width: float.PositiveInfinity,
        height: float.PositiveInfinity,
        child: child
    );

    public override Size Measure(Constraints c)
    {
        // Clamp explicit dimensions to the parent's allowed range.
        // This prevents SizedBox from overflowing its parent when the explicit size
        // is larger than the available space (e.g. inside a tight canvas preview).
        float? tw = Width.HasValue
            ? Math.Clamp(value: Width.Value, min: c.MinWidth, max: c.MaxWidth)
            : null;
        float? th = Height.HasValue
            ? Math.Clamp(value: Height.Value, min: c.MinHeight, max: c.MaxHeight)
            : null;

        if (Child == null)
        {
            _size = new Size(width: tw ?? c.MinWidth, height: th ?? c.MinHeight);
            return _size;
        }

        var childC = new Constraints(
            minWidth: tw ?? c.MinWidth,
            maxWidth: tw ?? c.MaxWidth,
            minHeight: th ?? c.MinHeight,
            maxHeight: th ?? c.MaxHeight
        );
        var childSz = Child.Measure(childC);

        _size = new Size(width: tw ?? childSz.Width, height: th ?? childSz.Height);
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

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? null;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
