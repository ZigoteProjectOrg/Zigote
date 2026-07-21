using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

public class Padding(EdgeInsets padding, Widget? child = null) : RenderWidget
{
    private Size _size;

    /// <summary>
    ///     The insets applied around <see cref="Child" /> — the constructor's <c>padding:</c>
    ///     argument (a property cannot share the enclosing type's name, hence <c>Insets</c>).
    /// </summary>
    public EdgeInsets Insets { get; set; } = padding;

    /// <summary>
    ///     Direction-relative insets. When set, they take precedence over <see cref="Insets" /> and
    ///     resolve against the ambient <see cref="Directionality" /> each measure (start/end swap
    ///     sides under RTL). The resolved value is written back to <see cref="Insets" /> so
    ///     Layout/Paint read one source of truth.
    /// </summary>
    public EdgeInsetsDirectional? DirectionalInsets { get; set; }

    public Widget? Child { get; set; } = child;

    public Padding(EdgeInsetsDirectional padding, Widget? child = null) : this(
        default(EdgeInsets),
        child
    )
    {
        DirectionalInsets = padding;
    }

    public static Padding All(float v, Widget? child = null)
    {
        return new Padding(EdgeInsets.All(v), child);
    }

    public static Padding Sym(float h, float v, Widget? child = null)
    {
        return new Padding(EdgeInsets.Symmetric(h, v), child);
    }

    public override Size Measure(Constraints c)
    {
        if (DirectionalInsets is { } d)
            Insets = d.Resolve(Directionality.Of(BuildContext.Current));

        var inner = c.Deflate(Insets);
        var childSize = Child?.Measure(inner) ?? Size.Zero;
        _size = c.Constrain(
            new Size(
                childSize.Width + Insets.Horizontal,
                childSize.Height + Insets.Vertical
            )
        );
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
        Child?.Layout(new Offset(origin.X + Insets.Left, origin.Y + Insets.Top));
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