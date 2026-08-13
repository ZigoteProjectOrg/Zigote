using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     A single-child layout widget with decoration, padding and margin.
/// </summary>
public sealed class Container : Widget
{
    private Color _background = Color.Transparent;
    private Color _borderColor = Color.Transparent;
    private float _borderWidth = 1f;
    private Widget? _child;
    private Size _childSize;
    private float _cornerRadius;
    private float? _height;
    private EdgeInsets _margin = EdgeInsets.Zero;
    private EdgeInsets _padding = EdgeInsets.Zero;
    private Size _totalSize;
    private float? _width;

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
        if (alignment is { } a && child is not null) child = new Align(alignment: a, child: child);
        if (constraints is { } bc && child is not null)
            child = new ConstrainedBox(constraints: bc, child: child);

        _child = child;
        if (padding is { } p) _padding = p;
        if (margin is { } m) _margin = m;
        _width = (float?)width;
        _height = (float?)height;

        if (color is { } col) _background = col;
        if (decoration is { } d)
        {
            if (d.Color is { } dc) _background = dc;
            float r = d.BorderRadius.Uniform;
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
        set => SetLayout(field: ref _child, value: value);
    }

    public Color Background
    {
        get => _background;
        set => SetPaint(field: ref _background, value: value);
    }

    public EdgeInsets Padding
    {
        get => _padding;
        set => SetLayout(field: ref _padding, value: value);
    }

    public EdgeInsets Margin
    {
        get => _margin;
        set => SetLayout(field: ref _margin, value: value);
    }

    public float? Width
    {
        get => _width;
        set => SetLayout(field: ref _width, value: value);
    }

    public float? Height
    {
        get => _height;
        set => SetLayout(field: ref _height, value: value);
    }

    public float CornerRadius
    {
        get => _cornerRadius;
        set => SetPaint(field: ref _cornerRadius, value: value);
    }

    public Color BorderColor
    {
        get => _borderColor;
        set => SetPaint(field: ref _borderColor, value: value);
    }

    public float BorderWidth
    {
        get => _borderWidth;
        set => SetPaint(field: ref _borderWidth, value: value);
    }

    public override Size Measure(Constraints c)
    {
        var inner = c.Deflate(Margin);

        float targetW = Width.HasValue
            ? Math.Clamp(value: Width.Value, min: inner.MinWidth, max: inner.MaxWidth)
            : inner.MaxWidth;
        float targetH = Height.HasValue
            ? Math.Clamp(value: Height.Value, min: inner.MinHeight, max: inner.MaxHeight)
            : inner.MaxHeight;

        if (Child != null)
        {
            var childC = new Constraints(
                minWidth: 0,
                maxWidth: Math.Max(val1: 0, val2: targetW - Padding.Left - Padding.Right),
                minHeight: 0,
                maxHeight: Math.Max(val1: 0, val2: targetH - Padding.Top - Padding.Bottom)
            );
            _childSize = Child.Measure(childC);

            float w = Width.HasValue ? targetW : _childSize.Width + Padding.Left + Padding.Right;
            float h = Height.HasValue ? targetH : _childSize.Height + Padding.Top + Padding.Bottom;
            _totalSize = new Size(
                width: w + Margin.Left + Margin.Right,
                height: h + Margin.Top + Margin.Bottom
            );
        }
        else
        {
            _childSize = Size.Zero;

            // Childless in unbounded space: fall back to the constraint minimum instead of Infinity.
            float fillW = float.IsFinite(targetW) ? targetW : inner.MinWidth;
            float fillH = float.IsFinite(targetH) ? targetH : inner.MinHeight;
            _totalSize = new Size(
                width: fillW + Margin.Left + Margin.Right,
                height: fillH + Margin.Top + Margin.Bottom
            );
        }

        _totalSize = c.Constrain(_totalSize);
        return _totalSize;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _totalSize.Width,
            height: _totalSize.Height
        );
        Child?.Layout(
            new Offset(
                x: origin.X + Margin.Left + Padding.Left,
                y: origin.Y + Margin.Top + Padding.Top
            )
        );
    }

    public override void Paint(PaintList paint)
    {
        var inner = new Rect(
            x: Bounds.X + Margin.Left,
            y: Bounds.Y + Margin.Top,
            width: Bounds.Width - Margin.Left - Margin.Right,
            height: Bounds.Height - Margin.Top - Margin.Bottom
        );

        if (Background.A > 0f)
            paint.AddRect(bounds: inner, color: Background, radius: CornerRadius);

        if (BorderColor.A > 0f)
        {
            paint.AddBorder(
                bounds: inner,
                color: BorderColor,
                radius: CornerRadius,
                width: BorderWidth
            );
        }

        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
