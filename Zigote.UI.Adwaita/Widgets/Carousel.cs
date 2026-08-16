using Zigote.Core.Events;
using Zigote.Core.State;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwCarousel — a paged horizontal container: every page gets the full carousel width, a
///     pointer drag scrolls the strip and snaps to the nearest page on release (fling advances a
///     page), Left/Right arrows page with the keyboard. Pair with
///     <see cref="AdwCarouselIndicatorDots" /> / <see cref="AdwCarouselIndicatorLines" />.
///     <para>
///         ponytail: while <see cref="Interactive" />, the carousel claims the pointer for
///         dragging, so controls inside pages are not clickable (no gesture arena to arbitrate a
///         drag vs a child tap — that is the upgrade path). Put page controls — turn buttons, an
///         overlaid caption — in a <c>Stack</c> above the carousel instead, where they are hit
///         first and the rest of the surface still drags.
///     </para>
/// </summary>
public sealed class AdwCarousel : Widget
{
    private const float DragSlop = 6f; // px before a press becomes a drag
    private const float FlingSpeed = 400f; // px/s that advances a page regardless of distance
    private const float WheelThreshold = 1f; // accumulated wheel ticks that page (a notch = 1)
    private const long WheelDebounceMs = 250; // one page per gesture window, à la libadwaita

    /// <summary>Current page, signal-backed so the indicators can react.</summary>
    internal readonly Signal<int> PositionSignal = new(0);

    private readonly SmoothScroller _scroller;
    private bool _armed;
    private float _dragStartOffset;
    private float _dragStartX;
    private bool _dragging;
    private float _lastPageWidth = -1f;
    private float _pageWidth;
    private Size _size;
    private bool _touchDragging;
    private float _wheelAccum;

    private long _wheelLastMs;

    // Not long.MinValue: `now - long.MinValue` overflows to a large NEGATIVE number, which reads as
    // "paged a moment ago" and made the debounce below swallow every wheel event ever — the whole
    // wheel and trackpad path was dead until the first drag. Halved, the subtraction stays in range.
    private long _wheelPagedMs = long.MinValue / 2;

    public AdwCarousel(params Widget[] pages) : this((IEnumerable<Widget>)pages) { }

    public AdwCarousel(IEnumerable<Widget> pages)
    {
        Pages = [.. pages];
        _scroller = new SmoothScroller(MarkNeedsLayoutClipped);
    }

    public List<Widget> Pages { get; }

    public Action<int>? OnPageChanged { get; set; }

    /// <summary>Whether pointer drags page the carousel (indicators/keyboard still work).</summary>
    public bool Interactive { get; set; } = true;

    public int Position
    {
        get => PositionSignal.Value;
        set => GoTo(value);
    }

    public override bool Focusable => Interactive && Pages.Count > 1;
    public override bool HandlesDirectionalKeys => true;

    private void GoTo(int index)
    {
        index = Math.Clamp(value: index, min: 0, max: Math.Max(val1: 0, val2: Pages.Count - 1));
        if (_pageWidth > 0f) _scroller.AnimateTo(index * _pageWidth);
        SetPosition(index);
    }

    private void SetPosition(int index)
    {
        if (PositionSignal.Peek() == index) return;
        PositionSignal.Value = index;
        OnPageChanged?.Invoke(index);
    }

    public override Size Measure(Constraints c)
    {
        float width;
        if (float.IsFinite(c.MaxWidth))
            width = c.MaxWidth;
        else
        {
            // Unbounded width: size to the widest page's intrinsic width.
            width = 0f;
            foreach (var page in Pages)
            {
                width = MathF.Max(
                    x: width,
                    y: page.Measure(
                        new Constraints(
                            minWidth: 0,
                            maxWidth: float.PositiveInfinity,
                            minHeight: 0,
                            maxHeight: c.MaxHeight
                        )
                    ).Width
                );
            }
        }

        float height = 0f;
        foreach (var page in Pages)
        {
            height = MathF.Max(
                x: height,
                y: page.Measure(
                    new Constraints(
                        minWidth: width,
                        maxWidth: width,
                        minHeight: 0,
                        maxHeight: c.MaxHeight
                    )
                ).Height
            );
        }

        if (float.IsFinite(c.MaxHeight)) height = c.MaxHeight;

        // Re-measure tight so every page fills the resolved page box.
        foreach (var page in Pages) page.Measure(Constraints.Tight(width: width, height: height));

        _pageWidth = width;
        _scroller.Max = MathF.Max(x: 0f, y: (Pages.Count - 1) * width);
        _scroller.Reclamp();
        if (MathF.Abs(width - _lastPageWidth) > 0.5f)
        {
            // First measure / resize: land exactly on the current page at the new width.
            _lastPageWidth = width;
            _scroller.JumpTo(PositionSignal.Peek() * width);
        }

        _size = c.Constrain(new Size(width: width, height: height));
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
        float x = origin.X - _scroller.Offset;
        foreach (var page in Pages)
        {
            page.Layout(new Offset(x: x, y: origin.Y));
            x += _pageWidth;
        }
    }

    public override void Paint(PaintList paint)
    {
        paint.AddClipStart(Bounds);
        foreach (var page in Pages)
        {
            // Cull pages fully outside the viewport.
            if (page.Bounds.Right < Bounds.X - 0.5f || page.Bounds.X > Bounds.Right + 0.5f)
                continue;
            page.Paint(paint);
        }

        paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;

        // ponytail: an Interactive carousel claims the whole pointer so a drag can start anywhere
        // (nearly every widget claims hits, so "children first" would leave no drag surface).
        // Controls inside pages are therefore not clickable while Interactive — set Interactive =
        // false to restore child hit-testing; a gesture arena is the upgrade path.
        if (Interactive && Pages.Count > 1) return this;

        foreach (var page in Pages)
        {
            var hit = page.HitTest(point);
            if (hit is not null) return hit;
        }

        return this;
    }

    // ── Mouse drag ──────────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        if (!Interactive || Pages.Count < 2) return;
        _armed = true;
        _dragging = false;
        _dragStartX = point.X;
        _dragStartOffset = _scroller.Offset;
    }

    public override void OnPointerMove(Offset point)
    {
        if (!_armed) return;
        float dx = point.X - _dragStartX;
        if (!_dragging && MathF.Abs(dx) > DragSlop) _dragging = true;
        if (_dragging) _scroller.JumpTo(_dragStartOffset - dx);
    }

    public override void OnPointerUp(Offset point)
    {
        // A finger drag arrives as OnTouchScroll but still lifts through here, and one released too
        // slowly to fling gets no OnTouchFling — without _touchDragging it would rest between pages.
        if (_dragging || _touchDragging) SnapToNearest();
        _armed = false;
        _dragging = false;
        _touchDragging = false;
    }

    public override void OnPointerCancel()
    {
        if (_dragging || _touchDragging) SnapToNearest();
        _armed = false;
        _dragging = false;
        _touchDragging = false;
    }

    private void SnapToNearest()
    {
        if (_pageWidth <= 0f) return;
        GoTo((int)MathF.Round(_scroller.Offset / _pageWidth));
    }

    // ── Mouse wheel ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     A wheel tick pages the carousel: dominant axis wins, wheel-down / touchpad-left is
    ///     forward. Deltas accumulate against a one-tick threshold so touchpad micro-deltas add
    ///     up, and one gesture pages at most once per <see cref="WheelDebounceMs" />.
    /// </summary>
    public override void OnScroll(float dx, float dy)
    {
        if (!Interactive || Pages.Count < 2 || _pageWidth <= 0f)
        {
            base.OnScroll(dx: dx, dy: dy);
            return;
        }

        // Dominant axis; dy is negated so wheel-down means forward (matches ScrollView's signs).
        float delta = MathF.Abs(dx) > MathF.Abs(dy) ? dx : -dy;
        long now = Environment.TickCount64;
        if (now - _wheelLastMs > WheelDebounceMs) _wheelAccum = 0f; // idle timeout: new gesture
        _wheelLastMs = now;
        if (now - _wheelPagedMs < WheelDebounceMs) return; // just paged: swallow the gesture tail

        _wheelAccum += delta;
        if (MathF.Abs(_wheelAccum) < WheelThreshold) return;

        GoTo(PositionSignal.Peek() + (_wheelAccum > 0f ? 1 : -1));
        _wheelAccum = 0f;
        _wheelPagedMs = now;
    }

    // ── Touch drag ──────────────────────────────────────────────────────────────

    public override bool CanTouchScroll(bool vertical) =>
        Interactive && !vertical && Pages.Count > 1;

    public override void OnTouchScroll(float dx, float dy)
    {
        _touchDragging = true; // the lift snaps, whether or not it is fast enough to fling
        _scroller.JumpTo(_scroller.Offset - dx);
    }

    public override void OnTouchFling(float velocityX, float velocityY)
    {
        // Settled here: the OnPointerUp that follows the fling must not snap back to where the
        // finger left off, which is still the previous page while the glide animates.
        _touchDragging = false;
        if (_pageWidth <= 0f) return;
        // Finger right (+vx) reveals the previous page; a fast fling always advances one page.
        if (velocityX <= -FlingSpeed)
            GoTo((int)MathF.Floor(_scroller.Offset / _pageWidth) + 1);
        else if (velocityX >= FlingSpeed)
            GoTo((int)MathF.Ceiling(_scroller.Offset / _pageWidth) - 1);
        else
            SnapToNearest();
    }

    // ── Keyboard ────────────────────────────────────────────────────────────────

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        switch (scancode)
        {
            case 80: // Left
                GoTo(PositionSignal.Peek() - 1);
                break;
            case 79: // Right
                GoTo(PositionSignal.Peek() + 1);
                break;
        }
    }

    public override void Detach()
    {
        base.Detach();
        _scroller.Dispose(); // ticker recreated lazily on the next animation
    }

    public override IEnumerable<Widget> GetChildren() => Pages;

    /// <summary>Only the current page is focus/semantics-reachable.</summary>
    public override IEnumerable<Widget> GetVisibleChildren()
    {
        int i = PositionSignal.Peek();
        if (i >= 0 && i < Pages.Count) yield return Pages[i];
    }

    public override int DebugStateHash() => HashCode.Combine(
        value1: _scroller.Offset,
        value2: Pages.Count
    );
}

/// <summary>8px page dots for an <see cref="AdwCarousel" />; clicking a dot jumps to its page.</summary>
public sealed class AdwCarouselIndicatorDots(AdwCarousel carousel) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        return CarouselIndicator.Build(
            context: context,
            carousel: carousel,
            width: 8f,
            height: 8f,
            radius: 4f
        );
    }
}

/// <summary>24×3px page lines for an <see cref="AdwCarousel" />; clicking a line jumps to its page.</summary>
public sealed class AdwCarouselIndicatorLines(AdwCarousel carousel) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        return CarouselIndicator.Build(
            context: context,
            carousel: carousel,
            width: 24f,
            height: 3f,
            radius: 1.5f
        );
    }
}

/// <summary>
///     The body both carousel indicators share: a row of pressable pips, the current one drawn in
///     the foreground colour and the rest dimmed, each jumping to its page. libadwaita's dots and
///     lines differ only in pip geometry, so they differ only in these arguments here.
/// </summary>
file static class CarouselIndicator
{
    public static Widget Build(
        BuildContext context,
        AdwCarousel carousel,
        float width,
        float height,
        float radius)
    {
        var theme = ThemeProvider.Of(context);
        return new Watch(() =>
            {
                int pos = carousel.PositionSignal.Value;
                var row = new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min);
                for (int i = 0; i < carousel.Pages.Count; i++)
                {
                    int index = i;
                    row.Children.Add(
                        new Pressable {
                            FocusRadius = radius,
                            SemanticsLabel = $"Page {index + 1}",
                            SelectedState = index == pos,
                            OnPressed = () => carousel.Position = index,
                            Child = new SizedBox(
                                width: width,
                                height: height,
                                // The carousel's own indicators: currentColor for the current page,
                                // 30% of it for the rest — the same relationship every Adwaita
                                // indicator (tab bar, view switcher badge) uses.
                                child: new DecoratedBox {
                                    Radius = radius,
                                    Fill = index == pos
                                        ? theme.OnSurface
                                        : AdwPalette.Fill(
                                            theme: theme,
                                            percent: AdwStyle.DimmerOpacity
                                        ),
                                }
                            ),
                        }
                    );
                }

                return row;
            }
        );
    }
}
