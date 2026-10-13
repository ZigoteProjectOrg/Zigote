namespace Zigote.UI.Adwaita;

/// <summary>
///     A boxed list whose rows are put in order by dragging — the shape libadwaita apps build from
///     an <c>AdwActionRow</c> with a <c>list-drag-handle-symbolic</c> prefix. Rows are the 50px
///     boxed-list rows with a hairline between them and a dim drag handle at the start. While a
///     row is dragged it lifts onto a card with a shadow and follows the pointer, and an accent
///     line marks where it will land; on release <see cref="OnReorder" /> fires with
///     (fromIndex, toIndex). Paints no background of its own — put it on a card.
/// </summary>
public sealed class AdwReorderableList : Widget
{
    // The row's start padding, the 16px handle, and the gap before the row content.
    private const float PointerGripWidth =
        AdwMetrics.RowPaddingX + AdwMetrics.IconSize + AdwMetrics.RowPaddingX;

    private readonly IList<Widget> _items;

    private bool _compact;
    private int _dragFrom = -1; // index of the row being dragged (-1 = idle)
    private int _dragInsert = -1; // insertion slot [0.._items.Count]
    private float _dragY; // current cursor Y (screen)
    private float _grabDy; // offset from row top to grab point
    private int _hoverIndex = -1;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public AdwReorderableList(IList<Widget> items, Action<int, int>? onReorder = null)
    {
        _items = items;
        OnReorder = onReorder;
    }

    /// <summary>Fired on a committed reorder with (fromIndex, toIndex).</summary>
    public Action<int, int>? OnReorder { get; set; }

    /// <summary>Per-row height in logical pixels.</summary>
    public float RowHeight { get; set; } = AdwMetrics.RowMinHeight;

    // Effective metrics: a 24×28 grip is a pointer affordance. On a phone both axes reach a finger
    // target so the handle can actually be grabbed.
    private float Grip => _compact ? AdwTouchMetrics.MinTarget : PointerGripWidth;

    private float RowH => _compact ? MathF.Max(x: RowHeight, y: AdwTouchMetrics.MinTarget) : RowHeight;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = AdwTouchMetrics.IsCompact;
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        float h = _items.Count * RowH;
        _size = c.Constrain(new Size(width: w, height: h));

        var rowC = new Constraints(
            minWidth: 0f,
            maxWidth: MathF.Max(x: 0f, y: _size.Width - Grip),
            minHeight: 0f,
            maxHeight: RowH
        );
        foreach (var item in _items)
            item.Measure(rowC);
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
        for (int i = 0; i < _items.Count; i++)
        {
            float y = origin.Y + (i * RowH);
            _items[i].Layout(new Offset(x: origin.X + Grip, y: y));
        }
    }

    public override void Paint(PaintList paint)
    {
        paint.AddClipStart(Bounds);

        for (int i = 0; i < _items.Count; i++)
        {
            if (i == _dragFrom) continue; // drawn separately as the ghost

            float rowY = Bounds.Y + (i * RowH);
            var rowRect = new Rect(
                x: Bounds.X,
                y: rowY,
                width: _size.Width,
                height: RowH
            );
            if (!paint.IsVisible(rowRect)) continue;

            // `row.activatable:hover` — the faint card wash, edge to edge like any boxed-list row.
            if (i == _hoverIndex && _dragFrom < 0)
            {
                paint.AddRect(
                    bounds: rowRect,
                    color: AdwStyle.RowFill(theme: _theme, hovered: true, pressed: false)
                );
            }

            // The boxed list's hairline between rows.
            if (i > 0)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X,
                        y: rowY,
                        width: _size.Width,
                        height: 1f
                    ),
                    color: AdwPalette.For(_theme).CardShade
                );
            }

            PaintGrip(paint: paint, rowY: rowY, active: false);
            _items[i].Paint(paint);
        }

        // Insertion indicator.
        if (_dragFrom >= 0 && _dragInsert >= 0)
        {
            float lineY = Bounds.Y + (_dragInsert * RowH);
            lineY = Math.Clamp(value: lineY, min: Bounds.Y, max: Bounds.Bottom - 2f);
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: lineY - 1f,
                    width: _size.Width,
                    height: 2f
                ),
                color: _theme.Accent,
                radius: 1f
            );
        }

        // Ghost of the dragged row, following the cursor.
        if (_dragFrom >= 0 && _dragFrom < _items.Count)
        {
            float ghostY = Math.Clamp(
                value: _dragY - _grabDy,
                min: Bounds.Y,
                max: Bounds.Bottom - RowH
            );
            var ghostRect = new Rect(
                x: Bounds.X,
                y: ghostY,
                width: _size.Width,
                height: RowH
            );
            // The lifted row is a card of its own: opaque card fill, card radius, a drop shadow.
            var p = AdwPalette.For(_theme);
            paint.AddElevation(
                bounds: ghostRect,
                radius: AdwMetrics.CardRadius,
                style: AdwMetrics.PopoverShadow
            );
            paint.AddRect(bounds: ghostRect, color: p.PopoverBg, radius: AdwMetrics.CardRadius);

            // Translate the dragged child + grip to the ghost position.
            var item = _items[_dragFrom];
            float dy = ghostY - (Bounds.Y + (_dragFrom * RowH));
            paint.PushTranslate(dx: 0f, dy: dy);
            PaintGrip(paint: paint, rowY: Bounds.Y + (_dragFrom * RowH), active: true);
            item.Paint(paint);
            paint.PopTranslate();
        }

        paint.AddClipEnd();
    }

    private void PaintGrip(PaintList paint, float rowY, bool active)
    {
        // `list-drag-handle-symbolic`, dim at rest and full strength while its row is held.
        Icons.Draw(
            paint: paint,
            glyph: MaterialIcons.DragIndicator,
            box: new Rect(
                x: Bounds.X,
                y: rowY,
                width: Grip,
                height: RowH
            ),
            color: _theme.OnSurface.WithAlpha(active ? 1f : AdwStyle.DimOpacity),
            size: AdwMetrics.IconSize
        );
    }

    private int RowIndexAt(float y)
    {
        int idx = (int)((y - Bounds.Y) / RowH);
        return idx >= 0 && idx < _items.Count ? idx : -1;
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;

        // The grip column always belongs to this list (drag handle). Elsewhere, let children hit-test.
        if (point.X >= Bounds.X + Grip)
        {
            int idx = RowIndexAt(point.Y);
            if (idx >= 0)
            {
                var hit = _items[idx].HitTest(point);
                if (hit is not null) return hit;
            }
        }

        return this;
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        if (_dragFrom >= 0) return MouseCursor.Move; // a row is being dragged
        if (point.X < Bounds.X + Grip) return MouseCursor.Move; // hovering the drag handle
        return null;
    }

    public override void OnPointerMove(Offset point)
    {
        if (_dragFrom >= 0)
        {
            _dragY = point.Y;
            // Insertion slot = nearest gap, computed from the row center under the cursor.
            float raw = (point.Y - Bounds.Y) / RowH;
            int insert = (int)MathF.Round(raw);
            _dragInsert = Math.Clamp(value: insert, min: 0, max: _items.Count);
            MarkNeedsPaint();
            return;
        }

        int idx = RowIndexAt(point.Y);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            MarkNeedsPaint();
        }
    }

    public override void OnPointerExit()
    {
        if (_dragFrom >= 0 || _hoverIndex == -1) return;
        _hoverIndex = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        int idx = RowIndexAt(point.Y);
        if (idx < 0) return;

        // Only the grip column starts a drag; clicks elsewhere are forwarded to children via HitTest.
        if (point.X <= Bounds.X + Grip)
        {
            _dragFrom = idx;
            _dragInsert = idx;
            _dragY = point.Y;
            _grabDy = point.Y - (Bounds.Y + (idx * RowH));
            MarkNeedsPaint();
        }
    }

    public override void OnPointerCancel()
    {
        // The gesture was taken away mid-lift (touch scroll claimed it, OS cancelled the touch):
        // drop the drag instead of leaving a ghost row pinned to a stale cursor position.
        if (_dragFrom < 0) return;
        _dragFrom = -1;
        _dragInsert = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (_dragFrom < 0) return;

        int from = _dragFrom;
        int insert = _dragInsert;
        _dragFrom = -1;
        _dragInsert = -1;

        // Convert an insertion slot into a destination index after removal of the source.
        int to = insert > from ? insert - 1 : insert;
        to = Math.Clamp(value: to, min: 0, max: _items.Count - 1);

        if (to != from && from >= 0 && from < _items.Count)
        {
            var moved = _items[from];
            _items.RemoveAt(from);
            _items.Insert(index: to, item: moved);
            MarkNeedsLayout();
            OnReorder?.Invoke(arg1: from, arg2: to);
        }
        else
            MarkNeedsPaint();
    }

    public override IEnumerable<Widget> GetChildren() => _items;

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: _items.Count,
            value2: _dragFrom,
            value3: _dragInsert,
            value4: _hoverIndex,
            value5: (int)_dragY
        );
    }
}
