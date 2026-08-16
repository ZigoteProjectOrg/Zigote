using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Clips its child and allows smooth scrolling along one or both axes — trackpad / wheel (eased
///     toward a target via a <see cref="SmoothScroller" />) or by dragging the scrollbar thumb. The
///     child is re-laid-out at the scrolled offset so descendants that derive screen-space state from
///     their Bounds (e.g. LiquidGlass backdrop capture, hit-testing) stay correct.
/// </summary>
public class ScrollView : Widget
{
    private readonly Scrollbar _hbar = new();
    private readonly SmoothScroller _sx;
    private readonly SmoothScroller _sy;
    private readonly Scrollbar _vbar = new();

    private Size _childSize;

    // Pending reveal-into-view request (content-space top + height), applied in Layout once Max is
    // known — so revealing a row in just-expanded (taller) content scrolls to the correct offset.
    private bool _hasPendingReveal;
    private float _revealTop, _revealHeight, _revealMargin;
    private ThemeData _theme = ThemeData.Dark;
    private Size _viewSize;

    public ScrollView(Widget? child = null)
    {
        Child = child;
        _sx = new SmoothScroller(MarkNeedsLayoutClipped);
        _sy = new SmoothScroller(MarkNeedsLayoutClipped);
    }

    public Widget? Child { get; set; }
    public bool ScrollVertical { get; set; } = true;
    public bool ScrollHorizontal { get; set; } = false;

    /// <summary>
    ///     Draw the scrollbars. Off for scrollers where the bar is noise rather than information — a
    ///     one-line strip of tabs, a chip row — which still scroll by wheel, drag and fling.
    /// </summary>
    public bool ShowScrollbars { get; set; } = true;

    /// <summary>Ease wheel scrolling (true) or jump instantly (false).</summary>
    public bool Smooth { get; set; } = true;

    public float ScrollSpeedMul { get; set; } = 40f;

    public float OffsetX
    {
        get => _sx.Offset;
        set => _sx.JumpTo(value);
    }

    public float OffsetY
    {
        get => _sy.Offset;
        set => _sy.JumpTo(value);
    }

    /// <summary>
    ///     The vertical offset a smooth scroll is easing toward (equals <see cref="OffsetY" /> at
    ///     rest).
    /// </summary>
    public float TargetOffsetY => _sy.Target;

    /// <summary>
    ///     The maximum scrollable vertical distance (content height − viewport height). 0 if it all
    ///     fits.
    /// </summary>
    public float MaxScrollExtentY => _sy.Max;

    /// <summary>
    ///     Fired each layout with (currentOffsetY, maxOffsetY) — use to drive infinite scroll /
    ///     paging.
    /// </summary>
    public Action<float, float>? OnScrolled { get; set; }

    /// <summary>
    ///     Scroll the vertical axis (smoothly) so the content-space band
    ///     <c>[top, top + height]</c> is within view, with <paramref name="margin" /> px of slack.
    ///     Content space is measured from the top of the scrolled child (offset 0). No-op if already
    ///     visible. The scroll is deferred to the next <see cref="Layout" /> so it lands correctly even
    ///     when the content just grew (e.g. a tree row revealed by expanding its ancestors).
    /// </summary>
    public void EnsureVisible(float top, float height, float margin = 8f)
    {
        _hasPendingReveal = true;
        _revealTop = top;
        _revealHeight = height;
        _revealMargin = margin;
        MarkNeedsLayout();
    }

    private void ApplyPendingReveal()
    {
        if (!_hasPendingReveal) return;
        _hasPendingReveal = false;

        float viewH = _viewSize.Height;
        if (viewH <= 0f) return;

        float cur = _sy.Offset;
        float top = _revealTop - _revealMargin;
        float bottom = _revealTop + _revealHeight + _revealMargin;

        float target;
        if (top < cur) target = top; // off the top — pull the band's top edge in
        else if (bottom > cur + viewH)
            target = bottom - viewH; // off the bottom — pull its bottom edge in
        else return; // already fully visible

        _sy.AnimateTo(target);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _viewSize = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));

        if (Child is not null)
        {
            // Give the child unbounded space on scrollable axes.
            var childC = new Constraints(
                maxWidth: ScrollHorizontal ? float.PositiveInfinity : c.MaxWidth,
                maxHeight: ScrollVertical ? float.PositiveInfinity : c.MaxHeight
            );
            _childSize = Child.Measure(childC);

            // A child that reports an infinite extent on a scrollable axis (a fill widget mistakenly
            // placed in a scroll) would make the scroll extent infinite — a drag drives the offset to ∞
            // and the scrollbar computes ∞/∞ = NaN. Clamp to the viewport so there's simply nothing to
            // scroll on that axis instead of poisoning the scroll math.
            if (!float.IsFinite(_childSize.Width) || !float.IsFinite(_childSize.Height))
            {
                _childSize = new Size(
                    width: float.IsFinite(_childSize.Width) ? _childSize.Width : _viewSize.Width,
                    height: float.IsFinite(_childSize.Height) ? _childSize.Height : _viewSize.Height
                );
            }
        }

        return _viewSize;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _viewSize.Width,
            height: _viewSize.Height
        );
        _sx.Max = MathF.Max(x: 0f, y: _childSize.Width - _viewSize.Width);
        _sy.Max = MathF.Max(x: 0f, y: _childSize.Height - _viewSize.Height);
        _sx.Reclamp();
        _sy.Reclamp();
        ApplyPendingReveal(); // adjust _sy.Offset before laying the child out at the final offset
        Child?.Layout(new Offset(x: origin.X - _sx.Offset, y: origin.Y - _sy.Offset));
        OnScrolled?.Invoke(arg1: _sy.Offset, arg2: _sy.Max);
    }

    public override void Paint(PaintList paint)
    {
        paint.AddClipStart(Bounds);
        Child?.Paint(paint);
        paint.AddClipEnd();

        if (!ShowScrollbars) return;

        if (ScrollVertical)
        {
            _vbar.PaintVertical(
                paint: paint,
                area: Bounds,
                viewport: _viewSize.Height,
                content: _childSize.Height,
                offset: _sy.Offset,
                tint: _theme.OnSurface
            );
        }

        if (ScrollHorizontal)
        {
            _hbar.PaintHorizontal(
                paint: paint,
                area: Bounds,
                viewport: _viewSize.Width,
                content: _childSize.Width,
                offset: _sx.Offset,
                tint: _theme.OnSurface
            );
        }
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        if (OverVBar(point) || OverHBar(point)) return this; // scrollbar drag claims the strip

        var oldScroll = CurrentScrollParent;
        CurrentScrollParent = this;
        var hit = Child?.HitTest(point);
        if (hit == null) CurrentScrollParent = oldScroll;
        // Only the final hit widget gets a ScrollParent from the App, so a nested scroller must
        // record its own here — otherwise the chain ends at this view and a drag it can't consume
        // (wrong axis, already at the edge) is dropped instead of bubbling to the outer scroller.
        else ScrollParent = oldScroll;
        return hit ?? this;
    }

    // The strip is a cursor affordance: 14 px is precise under a mouse and, under a finger, sits
    // exactly where a full-width row's trailing control does — a tap there would jump-scroll
    // instead of hitting the row. Fingers drag the content itself (CanTouchScroll) and need none of it.
    public override void OnPointerEnter()
    {
        // Enter has no position; a move follows immediately and resolves which strip, if either.
    }

    public override void OnPointerExit() => SetBarHover(vertical: false, horizontal: false);

    /// <summary>Widen whichever bar the pointer is on, so a 3 px target becomes a grabbable one.</summary>
    private void SetBarHover(bool vertical, bool horizontal)
    {
        if (_vbar.Hovered == vertical && _hbar.Hovered == horizontal) return;
        _vbar.Hovered = vertical;
        _hbar.Hovered = horizontal;
        MarkNeedsPaint();
    }

    private bool OverVBar(Offset p)
    {
        return !App.PointerIsTouch && ScrollVertical && _sy.Max > 0f &&
               p.X >= Bounds.Right - Scrollbar.HitWidth;
    }

    private bool OverHBar(Offset p)
    {
        return !App.PointerIsTouch && ScrollHorizontal && _sx.Max > 0f &&
               p.Y >= Bounds.Bottom - Scrollbar.HitWidth;
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        if (_vbar.Dragging || _hbar.Dragging || OverVBar(point) || OverHBar(point))
            return MouseCursor.Pointer;
        return null;
    }

    public override void OnPointerDown(Offset point)
    {
        if (OverVBar(point))
        {
            (float ts, float tl) = Scrollbar.VTrack(Bounds);
            (float start, float len) = _vbar.Geometry(
                trackStart: ts,
                trackLen: tl,
                viewport: _viewSize.Height,
                content: _childSize.Height,
                offset: _sy.Offset
            );
            _vbar.BeginDrag(pointer: point.Y, thumbStart: start, thumbLen: len);
            _sy.JumpTo(
                _vbar.OffsetAt(
                    pointer: point.Y,
                    trackStart: ts,
                    trackLen: tl,
                    viewport: _viewSize.Height,
                    content: _childSize.Height
                )
            );
        }
        else if (OverHBar(point))
        {
            (float ts, float tl) = Scrollbar.HTrack(Bounds);
            (float start, float len) = _hbar.Geometry(
                trackStart: ts,
                trackLen: tl,
                viewport: _viewSize.Width,
                content: _childSize.Width,
                offset: _sx.Offset
            );
            _hbar.BeginDrag(pointer: point.X, thumbStart: start, thumbLen: len);
            _sx.JumpTo(
                _hbar.OffsetAt(
                    pointer: point.X,
                    trackStart: ts,
                    trackLen: tl,
                    viewport: _viewSize.Width,
                    content: _childSize.Width
                )
            );
        }
    }

    public override void OnPointerMove(Offset point)
    {
        SetBarHover(
            vertical: OverVBar(point) || _vbar.Dragging,
            horizontal: OverHBar(point) || _hbar.Dragging
        );

        if (_vbar.Dragging)
        {
            (float ts, float tl) = Scrollbar.VTrack(Bounds);
            _sy.JumpTo(
                _vbar.OffsetAt(
                    pointer: point.Y,
                    trackStart: ts,
                    trackLen: tl,
                    viewport: _viewSize.Height,
                    content: _childSize.Height
                )
            );
        }
        else if (_hbar.Dragging)
        {
            (float ts, float tl) = Scrollbar.HTrack(Bounds);
            _sx.JumpTo(
                _hbar.OffsetAt(
                    pointer: point.X,
                    trackStart: ts,
                    trackLen: tl,
                    viewport: _viewSize.Width,
                    content: _childSize.Width
                )
            );
        }
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_vbar.Dragging && !_hbar.Dragging) return;
        _vbar.EndDrag();
        _hbar.EndDrag();
        MarkNeedsPaint();
    }

    public override void OnScroll(float dx, float dy)
    {
        bool moved = false;
        if (ScrollHorizontal) moved |= _sx.MoveBy(delta: dx * ScrollSpeedMul, animate: Smooth);
        if (ScrollVertical) moved |= _sy.MoveBy(delta: -dy * ScrollSpeedMul, animate: Smooth);

        // Bubble up only when we are at the edge and couldn't scroll.
        if (!moved) base.OnScroll(dx: dx, dy: dy);
    }

    public override bool CanTouchScroll(bool vertical)
    {
        // Not while a scrollbar thumb is being dragged — those finger moves belong to the
        // thumb (OnPointerMove), not to content scrolling.
        if (_vbar.Dragging || _hbar.Dragging) return false;
        return vertical ? ScrollVertical && _sy.Max > 0f : ScrollHorizontal && _sx.Max > 0f;
    }

    public override void OnTouchScroll(float dx, float dy)
    {
        // Content follows the finger 1:1 (drag down = reveal content above = offset shrinks),
        // hence the negation; no ScrollSpeedMul — that multiplier converts wheel ticks, not
        // pixels. Unanimated: the offset must track the finger exactly, with no easing lag.
        bool moved = false;
        if (ScrollHorizontal) moved |= _sx.MoveBy(delta: -dx, animate: false);
        if (ScrollVertical) moved |= _sy.MoveBy(delta: -dy, animate: false);
        if (!moved) base.OnTouchScroll(dx: dx, dy: dy);
    }

    public override void OnTouchFling(float velocityX, float velocityY)
    {
        bool started = false;
        if (ScrollHorizontal) started |= _sx.Fling(-velocityX);
        if (ScrollVertical) started |= _sy.Fling(-velocityY);
        if (!started) base.OnTouchFling(velocityX: velocityX, velocityY: velocityY);
    }

    public override void OnPointerCancel()
    {
        if (!_vbar.Dragging && !_hbar.Dragging) return;
        _vbar.EndDrag();
        _hbar.EndDrag();
        MarkNeedsPaint();
    }

    public override void Detach()
    {
        base.Detach();
        _sx.Dispose();
        _sy.Dispose();
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
