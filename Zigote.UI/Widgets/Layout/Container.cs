using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     A single-child layout widget with decoration, padding and margin.
/// </summary>
public sealed class Container : Widget
{
    private Size _childSize;
    private Size _totalSize;
    private Widget? _child;
    private Color _background = Color.Transparent;
    private EdgeInsets _padding = EdgeInsets.Zero;
    private EdgeInsets _margin = EdgeInsets.Zero;
    private float? _width;
    private float? _height;
    private float _cornerRadius;
    private Color _borderColor = Color.Transparent;
    private float _borderWidth = 1f;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>
    ///         new Container(width: 100, height: 40, color: Colors.Blue, padding: EdgeInsets.All(8),
    ///         child: …)
    ///     </c>
    ///     or with
    ///     <c>
    ///         decoration: new BoxDecoration(color: …, borderRadius:
    ///         BorderRadius.Circular(8), border: Border.All(Colors.Grey))
    ///     </c>
    ///     . <paramref name="alignment" />
    ///     wraps the child in an <see cref="Align" />; <paramref name="constraints" /> in a
    ///     <see cref="ConstrainedBox" />. Box shadows / gradients on the decoration are not rendered.
    /// </summary>
    public Container(
        Widget? child = null,
        Color? color = null,
        EdgeInsets? padding = null,
        EdgeInsets? margin = null,
        double? width = null,
        double? height = null,
        BoxDecoration? decoration = null,
        Alignment? alignment = null,
        BoxConstraints? constraints = null)
    {
        if (alignment is { } a && child is not null) child = new Align(a, child);
        if (constraints is { } bc && child is not null) child = new ConstrainedBox(bc, child);

        _child = child;
        if (padding is { } p) _padding = p;
        if (margin is { } m) _margin = m;
        _width = (float?)width;
        _height = (float?)height;

        if (color is { } col) _background = col;
        if (decoration is { } d)
        {
            if (d.Color is { } dc) _background = dc;
            var r = d.BorderRadius.Uniform;
            if (r > 0f) _cornerRadius = r;
            if (d.Border is { } b)
            {
                _borderColor = b.Color;
                _borderWidth = b.Width;
            }
        }
    }

    public Widget? Child
    {
        get => _child;
        set => SetLayout(ref _child, value);
    }

    public Color Background
    {
        get => _background;
        set => SetPaint(ref _background, value);
    }

    public EdgeInsets Padding
    {
        get => _padding;
        set => SetLayout(ref _padding, value);
    }

    public EdgeInsets Margin
    {
        get => _margin;
        set => SetLayout(ref _margin, value);
    }

    public float? Width
    {
        get => _width;
        set => SetLayout(ref _width, value);
    }

    public float? Height
    {
        get => _height;
        set => SetLayout(ref _height, value);
    }

    public float CornerRadius
    {
        get => _cornerRadius;
        set => SetPaint(ref _cornerRadius, value);
    }

    public Color BorderColor
    {
        get => _borderColor;
        set => SetPaint(ref _borderColor, value);
    }

    public float BorderWidth
    {
        get => _borderWidth;
        set => SetPaint(ref _borderWidth, value);
    }

    public override Size Measure(Constraints c)
    {
        var inner = c.Deflate(Margin);

        var targetW = Width.HasValue
            ? Math.Clamp(Width.Value, inner.MinWidth, inner.MaxWidth)
            : inner.MaxWidth;
        var targetH = Height.HasValue
            ? Math.Clamp(Height.Value, inner.MinHeight, inner.MaxHeight)
            : inner.MaxHeight;

        if (Child != null)
        {
            var childC = new Constraints(
                0,
                Math.Max(0, targetW - Padding.Left - Padding.Right),
                0,
                Math.Max(0, targetH - Padding.Top - Padding.Bottom)
            );
            _childSize = Child.Measure(childC);

            var w = Width.HasValue ? targetW : _childSize.Width + Padding.Left + Padding.Right;
            var h = Height.HasValue ? targetH : _childSize.Height + Padding.Top + Padding.Bottom;
            _totalSize = new Size(
                w + Margin.Left + Margin.Right,
                h + Margin.Top + Margin.Bottom
            );
        }
        else
        {
            _childSize = Size.Zero;

            // Childless in unbounded space: fall back to the constraint minimum instead of Infinity.
            var fillW = float.IsFinite(targetW) ? targetW : inner.MinWidth;
            var fillH = float.IsFinite(targetH) ? targetH : inner.MinHeight;
            _totalSize = new Size(
                fillW + Margin.Left + Margin.Right,
                fillH + Margin.Top + Margin.Bottom
            );
        }

        _totalSize = c.Constrain(_totalSize);
        return _totalSize;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _totalSize.Width,
            _totalSize.Height
        );
        Child?.Layout(
            new Offset(
                origin.X + Margin.Left + Padding.Left,
                origin.Y + Margin.Top + Padding.Top
            )
        );
    }

    public override void Paint(PaintList paint)
    {
        var inner = new Rect(
            Bounds.X + Margin.Left,
            Bounds.Y + Margin.Top,
            Bounds.Width - Margin.Left - Margin.Right,
            Bounds.Height - Margin.Top - Margin.Bottom
        );

        if (Background.A > 0f)
            paint.AddRect(inner, Background, CornerRadius);

        if (BorderColor.A > 0f)
            paint.AddBorder(
                inner,
                BorderColor,
                CornerRadius,
                BorderWidth
            );

        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}