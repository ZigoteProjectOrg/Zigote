namespace Zigote.UI.Material;

/// <summary>
///     A vertical list of fixed-height child widgets that can be reordered by dragging a grip handle
///     on the
///     left of each row. Pointer capture (App routes Move/Up to the widget that received Down until
///     release)
///     drives the drag: while dragging, the picked row follows the cursor as a translucent ghost and
///     an
///     insertion indicator marks where it will land; on release <see cref="OnReorder" /> fires with
///     (fromIndex, toIndex). Content is clipped to <see cref="Widget.Bounds" />.
/// </summary>
public sealed class ReorderableList : Widget
{
    private const float PointerGripWidth = 24f;

    private readonly IList<Widget> _items;

    private bool _compact;
    private int _dragFrom = -1; // index of the row being dragged (-1 = idle)
    private int _dragInsert = -1; // insertion slot [0.._items.Count]
    private float _dragY; // current cursor Y (screen)
    private float _grabDy; // offset from row top to grab point
    private int _hoverIndex = -1;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public ReorderableList(IList<Widget> items, Action<int, int>? onReorder = null)
    {
        _items = items;
        OnReorder = onReorder;
    }

    /// <summary>Fired on a committed reorder with (fromIndex, toIndex).</summary>
    public Action<int, int>? OnReorder { get; set; }

    /// <summary>Per-row height in logical pixels.</summary>
    public float RowHeight { get; set; } = ControlMetrics.RegularHeight;

    // Effective metrics: a 24×28 grip is a pointer affordance. On a phone both axes reach a finger
    // target so the handle can actually be grabbed.
    private float Grip => _compact ? TouchMetrics.MinTarget : PointerGripWidth;

    private float RowH => _compact ? MathF.Max(x: RowHeight, y: TouchMetrics.MinTarget) : RowHeight;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;
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

            if (i == _hoverIndex && _dragFrom < 0)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X + 2f,
                        y: rowY + 1f,
                        width: _size.Width - 4f,
                        height: RowH - 2f
                    ),
                    color: _theme.OnSurface.WithAlpha(0.05f),
                    radius: Radii.Sm
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
                    x: Bounds.X + 2f,
                    y: lineY - 1f,
                    width: _size.Width - 4f,
                    height: 2f
                ),
                color: _theme.Primary,
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
            paint.AddElevation(bounds: ghostRect, radius: Radii.Sm, style: Elevation.Z2);
            paint.AddRect(
                bounds: ghostRect,
                color: _theme.SurfaceAlt.WithAlpha(0.96f),
                radius: Radii.Sm
            );
            paint.AddBorder(
                bounds: ghostRect,
                color: _theme.Primary.WithAlpha(0.6f),
                radius: Radii.Sm
            );

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
        // Six-dot grip (⠿) drawn as small squares — robust regardless of font glyph coverage.
        var color = active ? _theme.OnSurface.WithAlpha(0.8f) : _theme.TextMuted.WithAlpha(0.55f);
        float cx = Bounds.X + (Grip / 2f);
        float cy = rowY + (RowH / 2f);
        const float gap = 4f;
        const float dot = 2f;
        for (int col = 0; col < 2; col++)
        for (int r = 0; r < 3; r++)
        {
            float dx = cx + (col == 0 ? -gap / 2f : gap / 2f) - (dot / 2f);
            float dyy = cy + ((r - 1) * gap) - (dot / 2f);
            paint.AddRect(
                bounds: new Rect(
                    x: dx,
                    y: dyy,
                    width: dot,
                    height: dot
                ),
                color: color,
                radius: 1f
            );
        }
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
        if (point.X < Bounds.X + Grip) return MouseCursor.Pointer; // hovering the drag grip
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
