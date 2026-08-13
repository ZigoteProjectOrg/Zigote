using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Vertically scrollable list with virtual windowing — only rows inside the viewport are measured,
///     laid out, and painted. Rows are a uniform <see cref="ItemHeight" /> by default; assign
///     <see cref="HeightOf" /> for variable-height rows (the list then keeps a prefix-sum offset table
///     and binary-searches the visible window, so cost stays O(viewport), not O(count)). Smooth wheel
///     scrolling (<see cref="SmoothScroller" />) + a draggable scrollbar thumb.
/// </summary>
public class ListView : Widget
{
    // Rows kept built on each side of the visible window, so a slow scroll doesn't rebuild the
    // leading row every frame.
    private const int Overscan = 4;
    private readonly List<Widget> _items = [];
    private readonly SmoothScroller _sy;
    private readonly Scrollbar _vbar = new();

    // Builder mode: rows are materialized on demand into _built and dropped once they leave the
    // window + overscan. _items stays empty.
    private Func<int, Widget>? _builder;
    private readonly Dictionary<int, Widget> _built = [];
    private readonly List<int> _evicted = [];
    private int _builderCount;
    private float _lastInnerWidth = -1f;
    private Func<int, float>? _heightOf;

    private float _itemHeight = 36f;

    // Prefix-sum of row tops for the variable-height path: _offsets[i] = top of row i,
    // _offsets[Count] = total content height. Null/empty while uniform or not yet built.
    private float[] _offsets = [];
    private bool _offsetsDirty = true;
    private EdgeInsets _padding = EdgeInsets.Zero;

    // Pending reveal-into-view row, applied in Layout once the scroll extent is known.
    private int _revealIndex = -1;
    private float _revealMargin;

    private ThemeData _theme = ThemeData.Dark;
    private Size _viewSize;

    /// <summary>
    ///     Named-argument constructor: <c>new ListView(children: [...])</c> or
    ///     <c>new ListView(itemExtent: 48, children: [...])</c>. All arguments optional, so
    ///     <c>new ListView { ItemHeight = … }</c> + <see cref="SetItems" /> still works.
    ///     A horizontal <paramref name="scrollDirection" /> is accepted but not applied — this list
    ///     virtualizes vertical rows.
    /// </summary>
    public ListView(
        List<Widget>? children = null,
        double? itemExtent = null,
        EdgeInsets? padding = null,
        Axis scrollDirection = Axis.Vertical)
    {
        _sy = new SmoothScroller(MarkNeedsLayout);
        if (itemExtent is { } e) ItemHeight = (float)e;
        if (children is not null) SetItems(children);
        if (padding is { } p) _padding = p;
        _ = scrollDirection;
    }

    /// <summary>Uniform row height, used when <see cref="HeightOf" /> is null.</summary>
    public float ItemHeight
    {
        get => _itemHeight;
        set
        {
            if (Math.Abs(_itemHeight - value) < float.Epsilon) return;
            _itemHeight = value;
            InvalidateExtents();
        }
    }

    /// <summary>
    ///     Optional per-row height provider (index → logical-pixel height). When set, rows may vary in
    ///     height and the list virtualizes against a prefix-sum table. Call
    ///     <see cref="InvalidateExtents" />
    ///     if the heights it returns change without the item list changing.
    /// </summary>
    public Func<int, float>? HeightOf
    {
        get => _heightOf;
        set
        {
            _heightOf = value;
            InvalidateExtents();
        }
    }

    /// <summary>Insets around the row content — rows are indented left/right and the first/last row
    ///     gets breathing room that scrolls with the content.</summary>
    public EdgeInsets Padding
    {
        get => _padding;
        set => SetLayout(ref _padding, value);
    }

    public float ScrollSpeed { get; set; } = 40f;

    /// <summary>Ease wheel scrolling (true) or jump instantly (false).</summary>
    public bool Smooth { get; set; } = true;

    public IReadOnlyList<Widget> Items => _items;

    /// <summary>The current vertical scroll offset.</summary>
    public float OffsetY
    {
        get => _sy.Offset;
        set => _sy.JumpTo(value);
    }

    /// <summary>Maximum scrollable distance (content height − viewport height). 0 if it all fits.</summary>
    public float MaxScrollExtentY => _sy.Max;

    /// <summary>
    ///     Fired each layout with (currentOffsetY, maxOffsetY) — use to drive infinite scroll /
    ///     paging. The same seam <see cref="ScrollView.OnScrolled" /> offers, so a virtualised list
    ///     and a plain scroll view page the same way.
    /// </summary>
    public Action<float, float>? OnScrolled { get; set; }

    private bool Variable => _heightOf is not null;

    /// <summary>Row count — the builder's count in builder mode, else the materialized item count.</summary>
    public int Count => _builder is not null ? _builderCount : _items.Count;

    /// <summary>Width available to a row (viewport minus <see cref="Padding" />).</summary>
    public float ViewportWidth => MathF.Max(0f, _viewSize.Width - _padding.Horizontal);

    private float ContentHeight =>
        (Variable ? Offsets[Count] : Count * _itemHeight) + _padding.Vertical;

    private float[] Offsets
    {
        get
        {
            EnsureOffsets();
            return _offsets;
        }
    }

    /// <summary>
    ///     Flutter's <c>ListView.builder</c>: rows are built on demand for the visible window only,
    ///     so construction is O(viewport) too — a million-row list costs the same as a ten-row one.
    ///     Rows leaving the window are detached and dropped, so any state they hold (hover, focus,
    ///     a nested scroll offset) dies with them — keep row state in your model, not in the widget.
    /// </summary>
    public static ListView Builder(int itemCount, Func<int, Widget> itemBuilder,
        double? itemExtent = null)
    {
        var lv = new ListView(itemExtent: itemExtent);
        lv.SetBuilder(itemCount, itemBuilder);
        return lv;
    }

    /// <summary>
    ///     Switch the list into builder mode — see <see cref="Builder" />. Call again to change the
    ///     count or the builder (e.g. after a filter); already-built rows are dropped.
    /// </summary>
    public void SetBuilder(int itemCount, Func<int, Widget> itemBuilder, bool keepScroll = false)
    {
        _items.Clear();
        DropBuilt();
        _builder = itemBuilder;
        _builderCount = Math.Max(0, itemCount);
        InvalidateExtents();
        if (!keepScroll) _sy.JumpTo(0f);
    }

    /// <summary>Row <paramref name="i" />, built and attached on first use in builder mode.</summary>
    private Widget ItemAt(int i)
    {
        if (_builder is null) return _items[i];
        if (_built.TryGetValue(i, out var w)) return w;

        w = _builder(i);
        _built[i] = w;
        if (Owner is not null) w.Attach(Owner, this);
        // Measure here so a row first reached from Layout/Paint (a scroll that outran the measure
        // pass) still has a size this frame instead of painting as a zero box.
        w.Measure(new Constraints(maxWidth: ViewportWidth, maxHeight: Extent(i)));
        return w;
    }

    private void DropBuilt()
    {
        foreach (var w in _built.Values) w.Detach();
        _built.Clear();
    }

    /// <summary>Drop built rows outside the window — the whole point of builder mode.</summary>
    private void EvictOutside(int first, int last)
    {
        if (_built.Count == 0) return;
        _evicted.Clear();
        foreach (var (i, _) in _built)
            if (i < first - Overscan || i > last + Overscan)
                _evicted.Add(i);
        foreach (var i in _evicted)
        {
            _built[i].Detach();
            _built.Remove(i);
        }
    }

    /// <summary>
    ///     Replace the row set. By default the list jumps back to the top (a new list, e.g. fresh
    ///     search suggestions); pass <paramref name="keepScroll" /> for live-refreshed data so the
    ///     user's position survives the swap (the next Layout re-clamps if the list got shorter).
    /// </summary>
    public void SetItems(IEnumerable<Widget> items, bool keepScroll = true)
    {
        LeaveBuilderMode();
        var previous = _items.ToArray();
        _items.Clear();
        _items.AddRange(items);
        // Attach the incoming set before retiring the outgoing one, so a row present in both is
        // re-parented rather than torn down (the same order Watch.Apply uses).
        if (Owner is not null)
            foreach (var w in _items)
                w.Attach(Owner, this);
        Retire(previous);
        InvalidateExtents();
        if (!keepScroll) _sy.JumpTo(0f);
    }

    public void AddItem(Widget item)
    {
        LeaveBuilderMode();
        _items.Add(item);
        // Rows added after the list itself was attached would otherwise stay ownerless: no Watch
        // inside them ever starts, and anything that needs the App — a Draggable, which refuses to
        // begin a drag without an Owner — silently does nothing.
        if (Owner is not null) item.Attach(Owner, this);
        InvalidateExtents();
    }

    public void Clear()
    {
        LeaveBuilderMode();
        var previous = _items.ToArray();
        _items.Clear();
        Retire(previous);
        InvalidateExtents();
    }

    /// <summary>Detach rows that left the list and were not re-adopted by the incoming set.</summary>
    private void Retire(Widget[] previous)
    {
        if (previous.Length == 0) return;
        var kept = _items.Count > 0 ? new HashSet<Widget>(_items) : null;
        foreach (var w in previous)
            if (ReferenceEquals(w.Parent, this) && kept?.Contains(w) != true)
                w.Detach();
    }

    private void LeaveBuilderMode()
    {
        if (_builder is null) return;
        DropBuilt();
        _builder = null;
        _builderCount = 0;
    }

    /// <summary>
    ///     Scroll (smoothly) so row <paramref name="index" /> is in view, with
    ///     <paramref name="margin" /> px of slack. Deferred to the next <see cref="Layout" /> so it
    ///     lands correctly even when the row only exists because the list just grew.
    /// </summary>
    public void EnsureVisible(int index, float margin = 8f)
    {
        _revealIndex = index;
        _revealMargin = margin;
        MarkNeedsLayout();
    }

    private void ApplyPendingReveal()
    {
        var i = _revealIndex;
        _revealIndex = -1;
        if (i < 0 || i >= Count || _viewSize.Height <= 0f) return;

        var top = Top(i) - _revealMargin;
        var bottom = Top(i) + Extent(i) + _revealMargin;
        var cur = _sy.Offset;
        if (top < cur) ScrollTo(top);
        else if (bottom > cur + _viewSize.Height) ScrollTo(bottom - _viewSize.Height);
        return;

        void ScrollTo(float y)
        {
            if (Smooth) _sy.AnimateTo(y);
            else _sy.JumpTo(y);
        }
    }

    /// <summary>Discard the cached row-offset table — call after changing variable row heights in place.</summary>
    public void InvalidateExtents()
    {
        _offsetsDirty = true;
        MarkNeedsLayout();
    }

    private void EnsureOffsets()
    {
        if (!_offsetsDirty) return;
        _offsetsDirty = false;
        if (!Variable)
        {
            _offsets = [];
            return;
        }

        var n = Count;
        if (_offsets.Length != n + 1) _offsets = new float[n + 1];
        var acc = 0f;
        for (var i = 0; i < n; i++)
        {
            _offsets[i] = acc;
            acc += MathF.Max(0f, _heightOf!(i));
        }

        _offsets[n] = acc;
    }

    private float Top(int i)
    {
        return Variable ? Offsets[i] : i * _itemHeight;
    }

    private float Extent(int i)
    {
        return Variable ? Offsets[i + 1] - Offsets[i] : _itemHeight;
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);

        // On an unbounded axis (e.g. inside a parent ScrollView) size to content rather than infinity —
        // an infinite size poisons flex layout (∞ − ∞ → NaN) and crashes paint.
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        // Width first, and before the offset table: a HeightOf that measures wrapped text (or a grid
        // row's cell height) is width-dependent, so a resize has to rebuild the table.
        // Publish the width through _viewSize before EnsureOffsets so HeightOf can read ViewportWidth.
        _viewSize = new Size(c.Constrain(new Size(w, 0f)).Width, _viewSize.Height);
        var innerW = ViewportWidth;
        if (MathF.Abs(innerW - _lastInnerWidth) > 0.01f)
        {
            _lastInnerWidth = innerW;
            _offsetsDirty = true;
        }

        EnsureOffsets();
        var h = float.IsFinite(c.MaxHeight) ? c.MaxHeight : ContentHeight;
        _viewSize = c.Constrain(new Size(w, h));

        // Measure only the visible window — the whole point of virtualization (was O(count)).
        // The `i < Count` re-check (here and in Layout/Paint/HitTest below) tolerates the
        // item list changing under the loop: a row's Measure or the OnScrolled seam can run app
        // code that calls SetItems (load-more reconcile). SetItems marks layout, so the truncated
        // pass is repaired next frame instead of indexing out of range.
        var (first, last) = VisibleRange();
        for (var i = first; i <= last && i < Count; i++)
            ItemAt(i).Measure(new Constraints(maxWidth: innerW, maxHeight: Extent(i)));
        EvictOutside(first, last);
        return _viewSize;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _viewSize.Width,
            _viewSize.Height
        );
        _sy.Max = MathF.Max(0f, ContentHeight - _viewSize.Height);
        _sy.Reclamp();
        ApplyPendingReveal();
        OnScrolled?.Invoke(_sy.Offset, _sy.Max);

        var (first, last) = VisibleRange();
        for (var i = first; i <= last && i < Count; i++)
            ItemAt(i).Layout(
                new Offset(
                    origin.X + _padding.Left,
                    origin.Y + _padding.Top + Top(i) - _sy.Offset
                )
            );
    }

    public override void Paint(PaintList paint)
    {
        paint.AddClipStart(Bounds);
        var (first, last) = VisibleRange();
        for (var i = first; i <= last && i < Count; i++)
            ItemAt(i).Paint(paint);
        paint.AddClipEnd();

        _vbar.PaintVertical(
            paint,
            Bounds,
            _viewSize.Height,
            ContentHeight,
            _sy.Offset,
            _theme.OnSurface
        );
    }

    /// <summary>Index of the row containing content-space vertical position <paramref name="y" />.</summary>
    private int IndexAt(float y)
    {
        var n = Count;
        if (!Variable)
            return Math.Clamp((int)(y / _itemHeight), 0, n - 1);

        // Largest i with Offsets[i] <= y.
        var offsets = Offsets;
        int lo = 0, hi = n - 1, result = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (offsets[mid] <= y)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    private (int First, int Last) VisibleRange()
    {
        var n = Count;
        if (n == 0) return (0, -1);
        // Row tops are padding-relative, so shift the viewport into that space.
        var top = _sy.Offset - _padding.Top;
        var bottom = top + _viewSize.Height;
        var first = Math.Max(0, IndexAt(top));
        // +1 row of slack so a partially-scrolled bottom row is included.
        var last = Math.Min(n - 1, IndexAt(bottom) + 1);
        return (first, last);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        if (OverVBar(point)) return this; // scrollbar strip

        var oldScroll = CurrentScrollParent;
        CurrentScrollParent = this;

        var (first, last) = VisibleRange();
        for (var i = Math.Min(last, Count - 1); i >= first; i--)
        {
            var hit = ItemAt(i).HitTest(point);
            if (hit is not null)
            {
                // Keep the bubble chain alive past this list: the App only assigns a ScrollParent
                // to the final hit widget, so a drag this list can't take (horizontal, or already
                // at its edge) would otherwise never reach an outer scroller.
                ScrollParent = oldScroll;
                return hit;
            }
        }

        CurrentScrollParent = oldScroll;
        return this;
    }

    // Mouse-only affordance: under a finger the 14 px strip overlaps the trailing controls of every
    // full-width row, and a tap there would jump-scroll instead of hitting the row. Touch drags the
    // content directly (CanTouchScroll).
    private bool OverVBar(Offset p)
    {
        return !App.PointerIsTouch && _sy.Max > 0f && p.X >= Bounds.Right - Scrollbar.HitWidth;
    }

    public override void OnPointerDown(Offset point)
    {
        if (!OverVBar(point)) return;
        var (ts, tl) = Scrollbar.VTrack(Bounds);
        var (start, len) = _vbar.Geometry(
            ts,
            tl,
            _viewSize.Height,
            ContentHeight,
            _sy.Offset
        );
        _vbar.BeginDrag(point.Y, start, len);
        _sy.JumpTo(
            _vbar.OffsetAt(
                point.Y,
                ts,
                tl,
                _viewSize.Height,
                ContentHeight
            )
        );
    }

    public override void OnPointerEnter()
    {
        SetBarHover(true);
    }

    public override void OnPointerExit()
    {
        SetBarHover(false);
    }

    /// <summary>
    ///     Widen the bar while the pointer is on its strip. HitTest already claims the strip for
    ///     this widget, so enter/exit fire exactly when the pointer crosses it.
    /// </summary>
    private void SetBarHover(bool hovered)
    {
        if (_vbar.Hovered == hovered) return;
        _vbar.Hovered = hovered;
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        // The strip and the rows share this widget's bounds, so a move is the only thing that says
        // which of the two the pointer is actually over.
        SetBarHover(OverVBar(point) || _vbar.Dragging);
        if (!_vbar.Dragging) return;
        var (ts, tl) = Scrollbar.VTrack(Bounds);
        _sy.JumpTo(
            _vbar.OffsetAt(
                point.Y,
                ts,
                tl,
                _viewSize.Height,
                ContentHeight
            )
        );
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_vbar.Dragging) return;
        _vbar.EndDrag();
        SetBarHover(OverVBar(point));
        MarkNeedsPaint();
    }

    public override void OnScroll(float dx, float dy)
    {
        if (!_sy.MoveBy(-dy * ScrollSpeed, Smooth)) base.OnScroll(dx, dy);
    }

    public override bool CanTouchScroll(bool vertical)
    {
        // Not while the scrollbar thumb is being dragged — those moves belong to the thumb.
        return vertical && !_vbar.Dragging && _sy.Max > 0f;
    }

    public override void OnTouchScroll(float dx, float dy)
    {
        // Content follows the finger 1:1 — pixel deltas, no wheel-tick multiplier, no easing.
        if (!_sy.MoveBy(-dy, false)) base.OnTouchScroll(dx, dy);
    }

    public override void OnTouchFling(float velocityX, float velocityY)
    {
        if (!_sy.Fling(-velocityY)) base.OnTouchFling(velocityX, velocityY);
    }

    public override void OnPointerCancel()
    {
        if (!_vbar.Dragging) return;
        _vbar.EndDrag();
        MarkNeedsPaint();
    }

    public override void Detach()
    {
        base.Detach();
        _sy.Dispose();
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _builder is not null ? _built.Values : _items;
    }

    /// <summary>
    ///     Focus/semantics parity with virtualization: only rows inside the viewport window are
    ///     measured and laid out, so off-window rows carry stale Bounds and must stay invisible to
    ///     focus traversal and the semantics walk.
    /// </summary>
    public override IEnumerable<Widget> GetVisibleChildren()
    {
        var (first, last) = VisibleRange();
        for (var i = first; i <= last && i < Count; i++)
            yield return ItemAt(i);
    }
}
