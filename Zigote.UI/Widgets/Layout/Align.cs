using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Aligns its child within itself using a fractional <see cref="Alignment" />. By default it
///     expands to fill the available space; set <see cref="WidthFactor" />/<see cref="HeightFactor" />
///     to instead size to a multiple of the child's size. The general form of <see cref="Center" />.
/// </summary>
public class Align : Widget
{
    private Size _childSize;
    private Size _size;

    /// <summary>
    ///     Named-argument constructor: <c>new Align(alignment: Alignment.BottomRight, child: w)</c>.
    ///     <paramref name="alignment" /> defaults to <see cref="Alignment.Center" />.
    ///     A positional <c>new Align(Alignment.TopLeft, w)</c> keeps working. Alignment uses the
    ///     framework's 0..1 space — for the −1..1 alignment convention use <see cref="Alignment.Xy" />.
    /// </summary>
    public Align(
        Alignment? alignment = null,
        Widget? child = null,
        double? widthFactor = null,
        double? heightFactor = null)
    {
        Alignment = alignment ?? Alignment.Center;
        Child = child;
        WidthFactor = (float?)widthFactor;
        HeightFactor = (float?)heightFactor;
    }

    public Alignment Alignment { get; set; }
    public Widget? Child { get; set; }
    public float? WidthFactor { get; set; }
    public float? HeightFactor { get; set; }

    public override Size Measure(Constraints c)
    {
        _childSize = Child?.Measure(
            new Constraints(
                minWidth: 0,
                maxWidth: c.MaxWidth,
                minHeight: 0,
                maxHeight: c.MaxHeight
            )
        ) ?? Size.Zero;

        float w = WidthFactor.HasValue ? _childSize.Width * WidthFactor.Value
            : float.IsFinite(c.MaxWidth) ? c.MaxWidth : _childSize.Width;
        float h = HeightFactor.HasValue ? _childSize.Height * HeightFactor.Value
            : float.IsFinite(c.MaxHeight) ? c.MaxHeight : _childSize.Height;

        _size = c.Constrain(new Size(width: w, height: h));
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
        var o = Alignment.Within(outer: _size, child: _childSize);
        Child?.Layout(new Offset(x: origin.X + o.X, y: origin.Y + o.Y));
    }

    public override void Paint(PaintList paint) => Child?.Paint(paint);

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? Child?.HitTest(point) : null;

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
