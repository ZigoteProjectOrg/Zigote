using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Sizes its child to a fixed <c>width : height</c> aspect ratio within the available space
///     (e.g. <c>16f / 9f</c> for a video frame). Picks the largest size that fits both constraints.
/// </summary>
public class AspectRatio : Widget
{
    private Size _size;

    /// <summary>
    ///     Sizes its child to a fixed aspect ratio, e.g.
    ///     <c>new AspectRatio(aspectRatio: 16.0 / 9.0, child: w)</c>.
    /// </summary>
    public AspectRatio(double aspectRatio, Widget? child = null)
    {
        Ratio = (float)aspectRatio;
        Child = child;
    }

    /// <summary>Target width divided by height.</summary>
    public float Ratio { get; set; }

    public Widget? Child { get; set; }

    public override Size Measure(Constraints c)
    {
        var r = MathF.Max(0.0001f, Ratio);

        var w = float.IsFinite(c.MaxWidth)
            ? c.MaxWidth
            : float.IsFinite(c.MaxHeight)
                ? c.MaxHeight * r
                : 0f;
        var h = w / r;

        if (float.IsFinite(c.MaxHeight) && h > c.MaxHeight)
        {
            h = c.MaxHeight;
            w = h * r;
        }

        _size = c.Constrain(new Size(w, h));
        Child?.Measure(Constraints.Tight(_size.Width, _size.Height));
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
        return Bounds.Contains(point.X, point.Y) ? Child?.HitTest(point) : null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}
