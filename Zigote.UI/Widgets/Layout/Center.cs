using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>Centers its child in the space the parent grants.</summary>
public class Center : Widget
{
    private Size _childSize;
    private Size _ownSize;

    /// <summary>
    ///     Named-argument constructor: <c>new Center(child: w)</c> or
    ///     <c>new Center(widthFactor: 1, heightFactor: 1, child: w)</c>. When a factor is given the
    ///     Center sizes to that multiple of the child instead of filling that axis.
    /// </summary>
    public Center(Widget? child = null, double? widthFactor = null, double? heightFactor = null)
    {
        Child = child;
        WidthFactor = (float?)widthFactor;
        HeightFactor = (float?)heightFactor;
    }

    public Widget? Child { get; set; }
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Center;
    public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Center;

    /// <summary>When set, own width = child width × factor instead of filling.</summary>
    public float? WidthFactor { get; set; }

    /// <summary>When set, own height = child height × factor instead of filling.</summary>
    public float? HeightFactor { get; set; }

    public override Size Measure(Constraints c)
    {
        // Loosen the min constraints (like Align) so the child reports its natural size and can be
        // centred within own size. Passing the parent's (possibly tight) constraints straight through
        // forced the child to fill, leaving nothing to centre.
        _childSize = Child?.Measure(
            new Constraints(
                0,
                c.MaxWidth,
                0,
                c.MaxHeight
            )
        ) ?? Size.Zero;
        var w = WidthFactor.HasValue ? _childSize.Width * WidthFactor.Value
            : float.IsFinite(c.MaxWidth) ? c.MaxWidth : _childSize.Width;
        var h = HeightFactor.HasValue ? _childSize.Height * HeightFactor.Value
            : float.IsFinite(c.MaxHeight) ? c.MaxHeight : _childSize.Height;
        _ownSize = c.Constrain(new Size(w, h));
        return _ownSize;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _ownSize.Width,
            _ownSize.Height
        );

        var cx = HorizontalAlignment switch {
            HorizontalAlignment.Left => origin.X,
            HorizontalAlignment.Right => origin.X + _ownSize.Width - _childSize.Width,
            _ => origin.X + (_ownSize.Width - _childSize.Width) / 2f,
        };
        var cy = VerticalAlignment switch {
            VerticalAlignment.Top => origin.Y,
            VerticalAlignment.Bottom => origin.Y + _ownSize.Height - _childSize.Height,
            _ => origin.Y + (_ownSize.Height - _childSize.Height) / 2f,
        };

        Child?.Layout(new Offset(cx, cy));
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