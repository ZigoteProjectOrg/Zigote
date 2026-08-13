namespace Zigote.UI.BottomSheets;

/// <summary>
///     A header pinned above the sheet's scrolling body whose height follows the sheet: it is
///     <see cref="MinHeight" /> tall at the sheet's minimum extent and grows to
///     <see cref="MaxHeight" />
///     when the sheet is fully expanded — the collapsing-toolbar half of
///     <see cref="BottomSheets.ShowStickyFlexible{T}" />.
///     <para>
///         The header content is measured at the current height each frame, so a child that wants to
///         restyle rather than just stretch (a title that grows, a subtitle that appears) reads
///         <see cref="BottomSheetController.Extent" /> inside a <c>Watch</c>.
///     </para>
/// </summary>
public sealed class SheetStickyHeader : Widget
{
    private readonly Widget _child;
    private readonly BottomSheetController _sheet;
    private Size _size;

    public SheetStickyHeader(Widget child, BottomSheetController sheet, float minHeight,
        float maxHeight)
    {
        _child = child;
        _sheet = sheet;
        MinHeight = minHeight;
        MaxHeight = MathF.Max(x: minHeight, y: maxHeight);
    }

    public float MinHeight { get; }
    public float MaxHeight { get; }

    /// <summary>Header height at the sheet's current extent.</summary>
    public float CurrentHeight => _size.Height;

    public override Size Measure(Constraints c)
    {
        float range = _sheet.MaxExtent - _sheet.MinExtent;
        float t = range > 0.0001f
            ? Math.Clamp(value: (_sheet.Value - _sheet.MinExtent) / range, min: 0f, max: 1f)
            : 1f;
        float height = MathF.Min(x: MinHeight + ((MaxHeight - MinHeight) * t), y: c.MaxHeight);
        float width = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;

        _child.Measure(Constraints.Tight(width: width, height: height));
        _size = new Size(width: width, height: height);
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
        _child.Layout(origin);
    }

    public override void Paint(PaintList paint) => _child.Paint(paint);

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _child.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_child);
}
