using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Sizes its child to a fraction of the available space (e.g. <c>WidthFactor = 0.5f</c> → half
///     width) and aligns it within the full space. Factors left null fill that axis completely.
/// </summary>
public class FractionallySizedBox(
    Widget? child = null,
    float? widthFactor = null,
    float? heightFactor = null)
    : Widget
{
    private Size _childSize;
    private Size _size;

    public Widget? Child { get; set; } = child;
    public float? WidthFactor { get; set; } = widthFactor;
    public float? HeightFactor { get; set; } = heightFactor;
    public Alignment Alignment { get; set; } = Alignment.Center;

    public override Size Measure(Constraints c)
    {
        float availW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f;
        float availH = float.IsFinite(c.MaxHeight) ? c.MaxHeight : 0f;

        float cw = WidthFactor.HasValue ? availW * WidthFactor.Value : availW;
        float ch = HeightFactor.HasValue ? availH * HeightFactor.Value : availH;

        _childSize = Child?.Measure(Constraints.Tight(width: cw, height: ch)) ??
                     new Size(width: cw, height: ch);
        _size = c.Constrain(new Size(width: availW, height: availH));
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
