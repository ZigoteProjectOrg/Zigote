namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwPaned — GTK's paned container: two children sharing the box, separated by a draggable
///     handle. The handle is a GNOME hairline drawn in the middle of a wider grab gutter, so the
///     divider reads as a 1px border while still being easy to catch; on a touch layout the grab
///     band inflates further around the unchanged hairline. <see cref="Position" /> is the fraction
///     (0–1) of the box the first child gets and is written back as the user drags, so a caller can
///     persist it.
/// </summary>
public sealed class AdwPaned : Widget
{
    private bool _compact;
    private float _dragStart;
    private bool _dragging;
    private Widget? _first;
    private Rect _handleRect;
    private float _handleWidth = 5f;
    private bool _hovered;
    private float _minPaneSize = 180f;
    private float _position = 0.5f;
    private float _positionAtDrag;
    private Widget? _second;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private bool _vertical;

    public AdwPaned(Widget? first = null, Widget? second = null, bool vertical = false)
    {
        _first = first;
        _second = second;
        _vertical = vertical;
    }

    // Every one of these is read in Layout, so a plain auto-property would accept the write and
    // change nothing until something else happened to relayout. SetLayout schedules the pass.
    public Widget? First
    {
        get => _first;
        set => SetLayout(field: ref _first, value: value);
    }

    public Widget? Second
    {
        get => _second;
        set => SetLayout(field: ref _second, value: value);
    }

    /// <summary>
    ///     Share of the box given to <see cref="First" />, 0–1. Written directly during a drag (the
    ///     drag already schedules its own relayout), so the setter only has to cover external writes.
    /// </summary>
    public float Position
    {
        get => _position;
        set => SetLayout(field: ref _position, value: value);
    }

    /// <summary>Top/bottom split instead of left/right.</summary>
    public bool Vertical
    {
        get => _vertical;
        set => SetLayout(field: ref _vertical, value: value);
    }

    /// <summary>Width of the grab gutter. The painted hairline stays 1px inside it.</summary>
    public float HandleWidth
    {
        get => _handleWidth;
        set => SetLayout(field: ref _handleWidth, value: value);
    }

    /// <summary>Minimum logical size of each pane; constrains how far the handle can travel.</summary>
    public float MinPaneSize
    {
        get => _minPaneSize;
        set => SetLayout(field: ref _minPaneSize, value: value);
    }

    /// <summary>Fires with the new <see cref="Position" /> at the end of a drag.</summary>
    public Action<float>? OnPositionChanged { get; set; }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = MediaQuery.Of(BuildContext.Current).SizeClass == WindowSizeClass.Compact;
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
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

        float span = (Vertical ? _size.Height : _size.Width) - HandleWidth;
        (float min, float max) = PositionBounds(span);
        float firstSpan = MathF.Floor(span * Math.Clamp(value: Position, min: min, max: max));
        float secondSpan = MathF.Max(x: 0f, y: span - firstSpan);

        if (Vertical)
        {
            First?.Measure(Constraints.Tight(width: _size.Width, height: firstSpan));
            First?.Layout(origin);
            _handleRect = new Rect(
                x: origin.X,
                y: origin.Y + firstSpan,
                width: _size.Width,
                height: HandleWidth
            );
            Second?.Measure(Constraints.Tight(width: _size.Width, height: secondSpan));
            Second?.Layout(new Offset(x: origin.X, y: origin.Y + firstSpan + HandleWidth));
        }
        else
        {
            First?.Measure(Constraints.Tight(width: firstSpan, height: _size.Height));
            First?.Layout(origin);
            _handleRect = new Rect(
                x: origin.X + firstSpan,
                y: origin.Y,
                width: HandleWidth,
                height: _size.Height
            );
            Second?.Measure(Constraints.Tight(width: secondSpan, height: _size.Height));
            Second?.Layout(new Offset(x: origin.X + firstSpan + HandleWidth, y: origin.Y));
        }
    }

    public override void Paint(PaintList paint)
    {
        First?.Paint(paint);

        // The rule itself is one hairline centred in the gutter — GNOME never widens the line, it
        // only tints it while the handle is active.
        var line = Vertical
            ? new Rect(
                x: _handleRect.X,
                y: MathF.Floor(_handleRect.Y + ((HandleWidth - 1f) / 2f)),
                width: _handleRect.Width,
                height: 1f
            )
            : new Rect(
                x: MathF.Floor(_handleRect.X + ((HandleWidth - 1f) / 2f)),
                y: _handleRect.Y,
                width: 1f,
                height: _handleRect.Height
            );
        paint.AddRect(
            bounds: line,
            color: _dragging || _hovered ? _theme.Primary : _theme.Separator
        );

        Second?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        if (Grab().Contains(px: point.X, py: point.Y)) return this;
        return Second?.HitTest(point) ?? First?.HitTest(point) ?? this;
    }

    public override void OnPointerEnter()
    {
        _hovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        _hovered = false;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Grab().Contains(px: point.X, py: point.Y)) return;
        _dragging = true;
        _dragStart = Vertical ? point.Y : point.X;
        _positionAtDrag = _position;
    }

    public override void OnPointerMove(Offset point)
    {
        if (!_dragging) return;
        float span = (Vertical ? _size.Height : _size.Width) - HandleWidth;
        if (span <= 0f) return;
        (float min, float max) = PositionBounds(span);
        float next = Math.Clamp(
            value: _positionAtDrag + (((Vertical ? point.Y : point.X) - _dragStart) / span),
            min: min,
            max: max
        );
        if (next == _position) return;
        _position = next;
        // The panes resize, so this needs a relayout — a pointer move alone would only repaint.
        MarkNeedsLayout();
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_dragging) return;
        _dragging = false;
        OnPositionChanged?.Invoke(Position);
    }

    public override void OnPointerCancel() => _dragging = false;

    /// <summary>
    ///     A grabbed handle owns the gesture inside a scrolling container. The press only becomes a
    ///     drag when it landed on the handle, so a press anywhere else still leaves the gesture to
    ///     the scroller.
    /// </summary>
    public override bool CanTouchDrag(bool vertical) => _dragging;

    public override MouseCursor? GetCursor(Offset point)
    {
        // Resize cursor while over the handle, and for the whole drag — a pointer that strays off
        // the thin gutter mid-drag is still captured by this widget, so it stays the cursor source.
        if (_dragging || Grab().Contains(px: point.X, py: point.Y))
            return Vertical ? MouseCursor.ResizeNS : MouseCursor.ResizeEW;
        return null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        if (First is not null) yield return First;
        if (Second is not null) yield return Second;
    }

    /// <summary>The allowed [min, max] range for <see cref="Position" /> at the current size.</summary>
    private (float Min, float Max) PositionBounds(float span)
    {
        if (span <= MinPaneSize * 2f) return (0.05f, 0.95f);
        float min = MinPaneSize / span;
        return (min, 1f - min);
    }

    /// <summary>
    ///     Where the handle is grabbed, as opposed to drawn: the gutter on a pointer (the resize
    ///     cursor does the rest of the advertising), inflated to a finger target on a touch layout.
    /// </summary>
    private Rect Grab()
    {
        if (!_compact) return _handleRect;
        float grow = MathF.Max(x: 0f, y: (ControlMetrics.MinTouchTarget - HandleWidth) / 2f);
        return Vertical
            ? new Rect(
                x: _handleRect.X,
                y: _handleRect.Y - grow,
                width: _handleRect.Width,
                height: _handleRect.Height + (grow * 2f)
            )
            : new Rect(
                x: _handleRect.X - grow,
                y: _handleRect.Y,
                width: _handleRect.Width + (grow * 2f),
                height: _handleRect.Height
            );
    }
}
