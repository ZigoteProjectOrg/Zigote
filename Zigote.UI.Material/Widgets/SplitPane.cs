namespace Zigote.UI.Material;

/// <summary>
///     Two children separated by a draggable divider.
///     <see cref="SplitRatio" /> (0–1) determines how much space the first child gets.
///     Set <see cref="Vertical" /> = true for a top/bottom split.
/// </summary>
public sealed class SplitPane(ThemeData theme, Widget? first = null, Widget? second = null)
    : Widget
{
    private bool _compact;
    private Rect _dividerRect;
    private float _dragStart;

    private bool _dragging;
    private float _ratioAtDrag;
    private Size _size;

    public Widget? First { get; set; } = first;
    public Widget? Second { get; set; } = second;
    public float SplitRatio { get; set; } = 0.5f;
    public float DividerW { get; set; } = 4f;
    public bool Vertical { get; set; } = false;
    public ThemeData Theme { get; set; } = theme;

    /// <summary>Minimum logical size of each pane; constrains how far the divider can travel.</summary>
    public float MinPaneSize { get; set; } = 180f;

    public override Size Measure(Constraints c)
    {
        _compact = TouchMetrics.IsCompact;
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        return _size;
    }

    /// <summary>
    ///     Where the divider is <em>grabbed</em>, as opposed to drawn. The 4pt hairline is the whole
    ///     affordance on a pointer (helped by the resize cursor); a finger has neither the precision
    ///     nor the cursor, so on a phone the grab band is inflated around the unchanged hairline.
    /// </summary>
    private Rect DividerGrab()
    {
        if (!_compact) return _dividerRect;
        float grow = MathF.Max(x: 0f, y: (TouchMetrics.MinTarget - DividerW) / 2f);
        return Vertical
            ? new Rect(
                x: _dividerRect.X,
                y: _dividerRect.Y - grow,
                width: _dividerRect.Width,
                height: _dividerRect.Height + (grow * 2f)
            )
            : new Rect(
                x: _dividerRect.X - grow,
                y: _dividerRect.Y,
                width: _dividerRect.Width + (grow * 2f),
                height: _dividerRect.Height
            );
    }

    /// <summary>The allowed [min, max] range for <see cref="SplitRatio" /> given the current size.</summary>
    private (float min, float max) RatioBounds(float totalSize)
    {
        if (totalSize > MinPaneSize * 2f)
        {
            float min = MinPaneSize / totalSize;
            return (min, 1f - min);
        }

        return (0.05f, 0.95f);
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

        float totalSize = Vertical ? _size.Height - DividerW : _size.Width - DividerW;
        (float minRatio, float maxRatio) = RatioBounds(totalSize);
        float ratio = Math.Clamp(value: SplitRatio, min: minRatio, max: maxRatio);

        if (Vertical)
        {
            float firstH = (float)Math.Floor((_size.Height - DividerW) * ratio);
            float secondH = _size.Height - DividerW - firstH;

            First?.Measure(Constraints.Tight(width: _size.Width, height: firstH));
            First?.Layout(origin);

            _dividerRect = new Rect(
                x: origin.X,
                y: origin.Y + firstH,
                width: _size.Width,
                height: DividerW
            );

            Second?.Measure(Constraints.Tight(width: _size.Width, height: secondH));
            Second?.Layout(new Offset(x: origin.X, y: origin.Y + firstH + DividerW));
        }
        else
        {
            float firstW = (float)Math.Floor((_size.Width - DividerW) * ratio);
            float secondW = _size.Width - DividerW - firstW;

            First?.Measure(Constraints.Tight(width: firstW, height: _size.Height));
            First?.Layout(origin);

            _dividerRect = new Rect(
                x: origin.X + firstW,
                y: origin.Y,
                width: DividerW,
                height: _size.Height
            );

            Second?.Measure(Constraints.Tight(width: secondW, height: _size.Height));
            Second?.Layout(new Offset(x: origin.X + firstW + DividerW, y: origin.Y));
        }
    }

    public override void Paint(PaintList paint)
    {
        First?.Paint(paint);

        // Divider — an adaptive hairline colour so it stays visible in light mode too (the old
        // SurfaceAlt token is pure white in the light theme, which vanished between white panes).
        var dc = _dragging ? Theme.Primary.WithAlpha(0.7f) : Theme.Border;
        paint.AddRect(bounds: _dividerRect, color: dc);

        Second?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        if (DividerGrab().Contains(px: point.X, py: point.Y)) return this;
        var hit = Second?.HitTest(point) ?? First?.HitTest(point);
        return hit ?? this;
    }

    public override void OnPointerDown(Offset point)
    {
        if (!DividerGrab().Contains(px: point.X, py: point.Y)) return;
        _dragging = true;
        _dragStart = Vertical ? point.Y : point.X;
        _ratioAtDrag = SplitRatio;
    }

    public override void OnPointerMove(Offset point)
    {
        if (!_dragging) return;
        float delta = (Vertical ? point.Y : point.X) - _dragStart;
        float total = Vertical ? _size.Height - DividerW : _size.Width - DividerW;
        (float minRatio, float maxRatio) = RatioBounds(total);

        float next = Math.Clamp(
            value: _ratioAtDrag + (delta / total),
            min: minRatio,
            max: maxRatio
        );
        if (next == SplitRatio) return;
        SplitRatio = next;
        // The panes resize, so a relayout is required — a plain pointer-move would only repaint.
        MarkNeedsLayout();
    }

    public override void OnPointerUp(Offset _) => _dragging = false;

    /// <summary>The press was taken over (pinch, app background): let go of the divider.</summary>
    public override void OnPointerCancel() => _dragging = false;

    /// <summary>
    ///     A grabbed divider owns the gesture inside a scrolling container. The press only becomes a
    ///     drag when it landed on the divider, so a press anywhere else still leaves the gesture to
    ///     the scroller.
    /// </summary>
    public override bool CanTouchDrag(bool vertical) => _dragging;

    public override MouseCursor? GetCursor(Offset point)
    {
        // Resize cursor while hovering the divider (so the drag affordance is discoverable) and for the
        // whole duration of a drag — even if the pointer strays off the thin divider, the app keeps this
        // widget captured, so it stays the cursor source.
        if (_dragging || _dividerRect.Contains(px: point.X, py: point.Y))
            return Vertical ? MouseCursor.ResizeNS : MouseCursor.ResizeEW;
        return null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        if (First is not null) yield return First;
        if (Second is not null) yield return Second;
    }
}
