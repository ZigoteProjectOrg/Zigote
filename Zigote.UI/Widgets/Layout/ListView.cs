using Zigote.Core;
using Zigote.Core.Paint;
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
    private readonly List<Widget> _items = [];
    private readonly SmoothScroller _sy;
    private readonly Scrollbar _vbar = new();
    private Func<int, float>? _heightOf;

    private float _itemHeight = 36f;

    // Prefix-sum of row tops for the variable-height path: _offsets[i] = top of row i,
    // _offsets[Count] = total content height. Null/empty while uniform or not yet built.
    private float[] _offsets = [];
    private bool _offsetsDirty = true;
    private ThemeData _theme = ThemeData.Dark;
    private Size _viewSize;

    /// <summary>
    ///     Named-argument constructor: <c>new ListView(children: [...])</c> or
    ///     <c>new ListView(itemExtent: 48, children: [...])</c>. All arguments optional, so
    ///     <c>new ListView { ItemHeight = … }</c> + <see cref="SetItems" /> still works.
    ///     <paramref name="padding" /> and a horizontal <paramref name="scrollDirection" /> are accepted
    ///     but not applied — this list virtualizes uniform vertical rows.
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
        _ = padding;
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

    public float ScrollSpeed { get; set; } = 40f;

    /// <summary>Ease wheel scrolling (true) or jump instantly (false).</summary>
    public bool Smooth { get; set; } = true;

    public IReadOnlyList<Widget> Items => _items;

    private bool Variable => _heightOf is not null;

    private float ContentHeight => Variable ? Offsets[_items.Count] : _items.Count * _itemHeight;

    private float[] Offsets
    {
        get
        {
            EnsureOffsets();
            return _offsets;
        }
    }

    /// <summary>
    ///     Builds a list from an item count and item builder. This materializes
    ///     every item eagerly (the underlying list still virtualizes measure/layout/paint to the
    ///     viewport, so scrolling stays O(viewport)).
    /// </summary>
    public static ListView Builder(int itemCount, Func<int, Widget> itemBuilder,
        double? itemExtent = null)
    {
        var lv = new ListView(itemExtent: itemExtent);
        var items = new List<Widget>(Math.Max(0, itemCount));
        for (var i = 0; i < itemCount; i++) items.Add(itemBuilder(i));
        lv.SetItems(items);
        return lv;
    }

    /// <summary>
    ///     Replace the row set. By default the list jumps back to the top (a new list, e.g. fresh
    ///     search suggestions); pass <paramref name="keepScroll" /> for live-refreshed data so the
    ///     user's position survives the swap (the next Layout re-clamps if the list got shorter).
    /// </summary>
    public void SetItems(IEnumerable<Widget> items, bool keepScroll = true)
    {
        _items.Clear();
        _items.AddRange(items);
        InvalidateExtents();
        if (!keepScroll) _sy.JumpTo(0f);
    }

    public void AddItem(Widget item)
    {
        _items.Add(item);
        InvalidateExtents();
    }

    public void Clear()
    {
        _items.Clear();
        InvalidateExtents();
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

        var n = _items.Count;
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
        EnsureOffsets();

        // On an unbounded axis (e.g. inside a parent ScrollView) size to content rather than infinity —
        // an infinite size poisons flex layout (∞ − ∞ → NaN) and crashes paint.
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        var h = float.IsFinite(c.MaxHeight) ? c.MaxHeight : ContentHeight;
        _viewSize = c.Constrain(new Size(w, h));

        // Measure only the visible window — the whole point of virtualization (was O(count)).
        var (first, last) = VisibleRange();
        for (var i = first; i <= last; i++)
            _items[i].Measure(new Constraints(maxWidth: _viewSize.Width, maxHeight: Extent(i)));
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

        var (first, last) = VisibleRange();
        for (var i = first; i <= last; i++)
            _items[i].Layout(new Offset(origin.X, origin.Y + Top(i) - _sy.Offset));
    }

    public override void Paint(PaintList paint)
    {
        paint.AddClipStart(Bounds);
        var (first, last) = VisibleRange();
        for (var i = first; i <= last; i++)
            _items[i].Paint(paint);
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
        var n = _items.Count;
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
        var n = _items.Count;
        if (n == 0) return (0, -1);
        var top = _sy.Offset;
        var bottom = top + _viewSize.Height;
        var first = Math.Max(0, IndexAt(top));
        // +1 row of slack so a partially-scrolled bottom row is included.
        var last = Math.Min(n - 1, IndexAt(bottom) + 1);
        return (first, last);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        if (_sy.Max > 0f && point.X >= Bounds.Right - Scrollbar.HitWidth)
            return this; // scrollbar strip

        var oldScroll = CurrentScrollParent;
        CurrentScrollParent = this;

        var (first, last) = VisibleRange();
        for (var i = last; i >= first; i--)
        {
            var hit = _items[i].HitTest(point);
            if (hit is not null) return hit;
        }

        CurrentScrollParent = oldScroll;
        return this;
    }

    public override void OnPointerDown(Offset point)
    {
        if (_sy.Max <= 0f || point.X < Bounds.Right - Scrollbar.HitWidth) return;
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

    public override void OnPointerMove(Offset point)
    {
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
        MarkNeedsPaint();
    }

    public override void OnScroll(float dx, float dy)
    {
        if (!_sy.MoveBy(-dy * ScrollSpeed, Smooth)) base.OnScroll(dx, dy);
    }

    public override void Detach()
    {
        base.Detach();
        _sy.Dispose();
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _items;
    }
}