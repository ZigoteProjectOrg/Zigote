using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Controls the position and size of a child inside a <see cref="Stack" />. Unset edges leave the
///     child at its measured size, anchored to the stack's top-left; setting <see cref="Left" /> +
///     <see cref="Right" /> (or <see cref="Top" /> + <see cref="Bottom" />) stretches it between those
///     edges. A non-positioned Stack child simply sits at the top-left at its natural size.
/// </summary>
public class Positioned : Widget
{
    private Size _size;

    /// <summary>
    ///     Named-argument constructor: <c>new Positioned(left: 10, top: 20, child: w)</c>. Edge/size
    ///     arguments are optional; a positional <c>new Positioned(child) { Left = … }</c> keeps working.
    /// </summary>
    public Positioned(
        Widget child,
        double? left = null,
        double? top = null,
        double? right = null,
        double? bottom = null,
        double? width = null,
        double? height = null)
    {
        Child = child;
        Left = (float?)left;
        Top = (float?)top;
        Right = (float?)right;
        Bottom = (float?)bottom;
        Width = (float?)width;
        Height = (float?)height;
    }

    public Widget Child { get; set; }

    public float? Left { get; set; }
    public float? Top { get; set; }
    public float? Right { get; set; }
    public float? Bottom { get; set; }
    public float? Width { get; set; }
    public float? Height { get; set; }

    /// <summary>A child that fills the entire stack (<c>Positioned.fill</c>).</summary>
    public static Positioned Fill(Widget child)
    {
        return new Positioned(child) {
            Left = 0,
            Top = 0,
            Right = 0,
            Bottom = 0,
        };
    }

    public override Size Measure(Constraints c)
    {
        _size = Child.Measure(c);
        MeasuredSize = _size;
        return _size;
    }

    // Stack drives the actual rect; Layout(origin) is used for non-positioned placement.
    internal void LayoutAt(Rect rect)
    {
        Bounds = rect;
        Child.Layout(new Offset(rect.X, rect.Y));
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        Child.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        Child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}