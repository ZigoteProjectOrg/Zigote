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

    private float RowH => _compact ? MathF.Max(RowHeight, TouchMetrics.MinTarget) : RowHeight;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        var h = _items.Count * RowH;
        _size = c.Constrain(new Size(w, h));

        var rowC = new Constraints(
            0f,
            MathF.Max(0f, _size.Width - Grip),
            0f,
            RowH
        );
        foreach (var item in _items)
            item.Measure(rowC);
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
        for (var i = 0; i < _items.Count; i++)
        {
            var y = origin.Y + i * RowH;
            _items[i].Layout(new Offset(origin.X + Grip, y));
        }
    }

    public override void Paint(PaintList paint)
    {
        paint.AddClipStart(Bounds);

        for (var i = 0; i < _items.Count; i++)
        {
            if (i == _dragFrom) continue; // drawn separately as the ghost

            var rowY = Bounds.Y + i * RowH;
            var rowRect = new Rect(
                Bounds.X,
                rowY,
                _size.Width,
                RowH
            );
            if (!paint.IsVisible(rowRect)) continue;

            if (i == _hoverIndex && _dragFrom < 0)
                paint.AddRect(
                    new Rect(
                        Bounds.X + 2f,
                        rowY + 1f,
                        _size.Width - 4f,
                        RowH - 2f
                    ),
                    _theme.OnSurface.WithAlpha(0.05f),
                    Radii.Sm
                );

            PaintGrip(paint, rowY, false);
            _items[i].Paint(paint);
        }

        // Insertion indicator.
        if (_dragFrom >= 0 && _dragInsert >= 0)
        {
            var lineY = Bounds.Y + _dragInsert * RowH;
            lineY = Math.Clamp(lineY, Bounds.Y, Bounds.Bottom - 2f);
            paint.AddRect(
                new Rect(
                    Bounds.X + 2f,
                    lineY - 1f,
                    _size.Width - 4f,
                    2f
                ),
                _theme.Primary,
                1f
            );
        }

        // Ghost of the dragged row, following the cursor.
        if (_dragFrom >= 0 && _dragFrom < _items.Count)
        {
            var ghostY = Math.Clamp(_dragY - _grabDy, Bounds.Y, Bounds.Bottom - RowH);
            var ghostRect = new Rect(
                Bounds.X,
                ghostY,
                _size.Width,
                RowH
            );
            paint.AddElevation(ghostRect, Radii.Sm, Elevation.Z2);
            paint.AddRect(ghostRect, _theme.SurfaceAlt.WithAlpha(0.96f), Radii.Sm);
            paint.AddBorder(ghostRect, _theme.Primary.WithAlpha(0.6f), Radii.Sm);

            // Translate the dragged child + grip to the ghost position.
            var item = _items[_dragFrom];
            var dy = ghostY - (Bounds.Y + _dragFrom * RowH);
            paint.PushTranslate(0f, dy);
            PaintGrip(paint, Bounds.Y + _dragFrom * RowH, true);
            item.Paint(paint);
            paint.PopTranslate();
        }

        paint.AddClipEnd();
    }

    private void PaintGrip(PaintList paint, float rowY, bool active)
    {
        // Six-dot grip (⠿) drawn as small squares — robust regardless of font glyph coverage.
        var color = active ? _theme.OnSurface.WithAlpha(0.8f) : _theme.TextMuted.WithAlpha(0.55f);
        var cx = Bounds.X + Grip / 2f;
        var cy = rowY + RowH / 2f;
        const float gap = 4f;
        const float dot = 2f;
        for (var col = 0; col < 2; col++)
        for (var r = 0; r < 3; r++)
        {
            var dx = cx + (col == 0 ? -gap / 2f : gap / 2f) - dot / 2f;
            var dyy = cy + (r - 1) * gap - dot / 2f;
            paint.AddRect(
                new Rect(
                    dx,
                    dyy,
                    dot,
                    dot
                ),
                color,
                1f
            );
        }
    }

    private int RowIndexAt(float y)
    {
        var idx = (int)((y - Bounds.Y) / RowH);
        return idx >= 0 && idx < _items.Count ? idx : -1;
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;

        // The grip column always belongs to this list (drag handle). Elsewhere, let children hit-test.
        if (point.X >= Bounds.X + Grip)
        {
            var idx = RowIndexAt(point.Y);
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
            var raw = (point.Y - Bounds.Y) / RowH;
            var insert = (int)MathF.Round(raw);
            _dragInsert = Math.Clamp(insert, 0, _items.Count);
            MarkNeedsPaint();
            return;
        }

        var idx = RowIndexAt(point.Y);
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
        var idx = RowIndexAt(point.Y);
        if (idx < 0) return;

        // Only the grip column starts a drag; clicks elsewhere are forwarded to children via HitTest.
        if (point.X <= Bounds.X + Grip)
        {
            _dragFrom = idx;
            _dragInsert = idx;
            _dragY = point.Y;
            _grabDy = point.Y - (Bounds.Y + idx * RowH);
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

        var from = _dragFrom;
        var insert = _dragInsert;
        _dragFrom = -1;
        _dragInsert = -1;

        // Convert an insertion slot into a destination index after removal of the source.
        var to = insert > from ? insert - 1 : insert;
        to = Math.Clamp(to, 0, _items.Count - 1);

        if (to != from && from >= 0 && from < _items.Count)
        {
            var moved = _items[from];
            _items.RemoveAt(from);
            _items.Insert(to, moved);
            MarkNeedsLayout();
            OnReorder?.Invoke(from, to);
        }
        else
        {
            MarkNeedsPaint();
        }
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _items;
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            _items.Count,
            _dragFrom,
            _dragInsert,
            _hoverIndex,
            (int)_dragY
        );
    }
}
