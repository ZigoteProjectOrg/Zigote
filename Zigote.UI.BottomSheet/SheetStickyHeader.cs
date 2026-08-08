namespace Zigote.UI.BottomSheets;

/// <summary>
///     A header pinned above the sheet's scrolling body whose height follows the sheet: it is
///     <see cref="MinHeight" /> tall at the sheet's minimum extent and grows to <see cref="MaxHeight" />
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
        MaxHeight = MathF.Max(minHeight, maxHeight);
    }

    public float MinHeight { get; }
    public float MaxHeight { get; }

    /// <summary>Header height at the sheet's current extent.</summary>
    public float CurrentHeight => _size.Height;

    public override Size Measure(Constraints c)
    {
        var range = _sheet.MaxExtent - _sheet.MinExtent;
        var t = range > 0.0001f
            ? Math.Clamp((_sheet.Value - _sheet.MinExtent) / range, 0f, 1f)
            : 1f;
        var height = MathF.Min(MinHeight + (MaxHeight - MinHeight) * t, c.MaxHeight);
        var width = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;

        _child.Measure(Constraints.Tight(width, height));
        _size = new Size(width, height);
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
        _child.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _child.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_child);
    }
}