using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Material;

/// <summary>
///     Flat macOS-style tab strip: a hairline-separated row of text tabs. The selected tab is drawn in
///     <see cref="ThemeData.OnSurface" /> with a 2px <see cref="ThemeData.Primary" /> underline;
///     unselected
///     tabs use <see cref="ThemeData.Hint" /> and fill on hover. Pair with <see cref="TabView" /> to
///     show
///     matching content.
/// </summary>
public class TabBar : RenderWidget, ITickerProvider
{
    private readonly List<TabCell> _cells = [];
    private readonly AnimationController _slide;
    private float _maxScrollX;

    /// <summary>How far the strip is scrolled when the tabs are wider than the space they get.</summary>
    private float _scrollX;

    private int _selected;
    private Size _size;

    private IReadOnlyList<string> _tabs = [];
    private ThemeData _theme = ThemeData.Dark;
    private Ticker? _ticker;
    private float _underFrom;
    private bool _underInit;
    private float _underTo;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>
    ///         new TabBar(tabs: [ new Tab(text: "One"), new Tab(text: "Two") ],
    ///         onChanged: (i) => …)
    ///     </c>
    ///     . The theme is resolved from the ambient <c>ThemeProvider</c> during
    ///     Measure. Only each tab's text label is rendered.
    /// </summary>
    public TabBar(List<Tab> tabs, int initialIndex = 0, Action<int>? onChanged = null)
    {
        _selected = initialIndex;
        OnChanged = onChanged;
        _theme = ThemeData.Dark; // refreshed from the ambient ThemeProvider in Measure
        Tabs = tabs.ConvertAll(t => t.Label); // setter builds the cells
        _slide = new AnimationController(Motion.Standard, this) { Curve = Curves.EaseOut };
        _slide.OnTick += MarkNeedsPaint;
    }

    public IReadOnlyList<string> Tabs
    {
        get => _tabs;
        set
        {
            // Rebuild the cell strip when the labels change — Measure/Paint index _cells in lock-step
            // with Tabs, so a stale _cells count throws (shorter list) or shows old labels (same length).
            _tabs = value;
            RebuildCells();
            MarkNeedsLayout();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            MarkNeedsPaint();
        }
    }

    [Obsolete("Renamed — use SelectedIndex.")]
    public int Selected
    {
        get => SelectedIndex;
        set => SelectedIndex = value;
    }

    public Action<int>? OnChanged { get; set; }

    public ThemeData Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            MarkNeedsPaint();
        }
    }

    public float TabHeight { get; set; } = ControlMetrics.LargeHeight;
    public float MinTabWidth { get; set; } = 64f;

    public override bool Focusable => true;

    /// <summary>Owns Left/Right to move between tabs, so the app must not repurpose them for focus.</summary>
    public override bool HandlesDirectionalKeys => true;

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        _slide.AttachTicker(this);
    }

    public override void Detach()
    {
        base.Detach();
        _ticker?.Dispose();
        _ticker = null;
    }

    private void RebuildCells()
    {
        _cells.Clear();
        for (var i = 0; i < Tabs.Count; i++)
        {
            var idx = i;
            _cells.Add(
                new TabCell(
                    Tabs[i],
                    () => SelectedIndex == idx,
                    () => Select(idx),
                    this
                )
            );
        }
    }

    private void Select(int idx)
    {
        if (idx < 0 || idx >= _cells.Count) return;
        SelectedIndex = idx;
        OnChanged?.Invoke(idx);
        MarkNeedsPaint();
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is TabBar t)
        {
            SelectedIndex = t.SelectedIndex;
            OnChanged = t.OnChanged;
            Theme = t.Theme;
            Tabs = t.Tabs; // setter rebuilds cells + relayout
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(SelectedIndex, Tabs.Count, Focused);
    }

    public override Size Measure(Constraints c)
    {
        var theme = ThemeProvider.Of(BuildContext.Current);
        Theme = theme;

        // Intrinsic width: each tab fits its label plus symmetric padding, clamped to MinTabWidth.
        var fs = theme.FontSizeBody;
        var total = 0f;
        for (var i = 0; i < _cells.Count; i++)
        {
            var tw = TextMeasure.Width(Tabs[i], fs, FontWeight.Medium) + Spacing.Md * 2f;
            _cells[i].DesiredWidth = MathF.Max(MinTabWidth, tw);
            total += _cells[i].DesiredWidth;
        }

        var width = float.IsPositiveInfinity(c.MaxWidth) ? total : MathF.Min(total, c.MaxWidth);
        _size = c.Constrain(new Size(width, TouchMetrics.AtLeast(TabHeight, 48f)));

        // Tabs that don't fit used to be laid out and painted past Bounds — visible overflow and
        // unreachable tabs. Keep them in the strip and scroll it instead (drag on touch, wheel on
        // desktop, and the keyboard/selection follows below).
        _maxScrollX = MathF.Max(0f, total - _size.Width);
        _scrollX = Math.Clamp(_scrollX, 0f, _maxScrollX);
        if (_maxScrollX > 0f) ScrollSelectedIntoView();

        var cellH = _size.Height;
        foreach (var cell in _cells) cell.Measure(Constraints.Tight(cell.DesiredWidth, cellH));
        return _size;
    }

    private void ScrollSelectedIntoView()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _cells.Count) return;
        var left = 0f;
        for (var i = 0; i < SelectedIndex; i++) left += _cells[i].DesiredWidth;
        var right = left + _cells[SelectedIndex].DesiredWidth;
        if (left < _scrollX) _scrollX = left;
        else if (right > _scrollX + _size.Width) _scrollX = right - _size.Width;
        _scrollX = Math.Clamp(_scrollX, 0f, _maxScrollX);
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        var x = origin.X - _scrollX;
        foreach (var cell in _cells)
        {
            cell.Layout(new Offset(x, origin.Y));
            x += cell.DesiredWidth;
        }
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, Theme.Surface);
        paint.AddClipStart(Bounds);
        foreach (var c in _cells) c.Paint(paint);
        paint.AddClipEnd();

        // Hairline baseline separator under the whole strip.
        paint.AddRect(
            new Rect(
                Bounds.X,
                Bounds.Bottom - 1f,
                Bounds.Width,
                1f
            ),
            Theme.Separator
        );

        paint.AddClipStart(Bounds);
        PaintUnderline(paint);
        if (Focused) PaintSelectedFocusRing(paint);
        paint.AddClipEnd();
    }

    /// <summary>Draws the 2px accent underline, sliding it between tabs when the selection changes.</summary>
    private void PaintUnderline(PaintList paint)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _cells.Count) return;

        // Retarget the slide whenever the selection changed since the last paint.
        if (!_underInit)
        {
            _underInit = true;
            _underFrom = _underTo = SelectedIndex;
        }
        else if (Math.Abs(_underTo - SelectedIndex) > 0.001f)
        {
            _underFrom = UnderPos();
            _underTo = SelectedIndex;
            _slide.Dismiss();
            _slide.Forward();
        }

        var pos = UnderPos();
        var lo = (int)MathF.Floor(pos);
        var hi = Math.Min(lo + 1, _cells.Count - 1);
        lo = Math.Clamp(lo, 0, _cells.Count - 1);
        var frac = pos - lo;

        var a = _cells[lo].Bounds;
        var b = _cells[hi].Bounds;
        var x = a.X + (b.X - a.X) * frac;
        var w = a.Width + (b.Width - a.Width) * frac;

        paint.AddRect(
            new Rect(
                x,
                Bounds.Bottom - 2f,
                w,
                2f
            ),
            Theme.Primary
        );
    }

    private float UnderPos()
    {
        return _underInit ? _underFrom + (_underTo - _underFrom) * _slide.Value : SelectedIndex;
    }

    private void PaintSelectedFocusRing(PaintList paint)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _cells.Count) return;
        var b = _cells[SelectedIndex].Bounds;
        var inset = new Rect(
            b.X + Spacing.Xs,
            b.Y + Spacing.Xxs,
            b.Width - Spacing.Sm,
            b.Height - Spacing.Xs
        );
        paint.AddFocusRing(inset, Radii.Sm, Theme);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        foreach (var c in _cells)
        {
            var hit = c.HitTest(point);
            if (hit is not null) return hit;
        }

        return this;
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        switch (scancode)
        {
            case 80: // Left
                Select(SelectedIndex - 1);
                break;
            case 79: // Right
                Select(SelectedIndex + 1);
                break;
        }
    }

    // A strip wider than its box is a horizontal scroller — the finger drags it, the wheel's
    // horizontal axis nudges it, and anything else keeps bubbling to the page.
    public override bool CanTouchScroll(bool vertical)
    {
        return !vertical && _maxScrollX > 0f;
    }

    public override void OnTouchScroll(float dx, float dy)
    {
        if (_maxScrollX <= 0f)
        {
            base.OnTouchScroll(dx, dy);
            return;
        }

        _scrollX = Math.Clamp(_scrollX - dx, 0f, _maxScrollX);
        MarkNeedsLayout();
    }

    public override void OnScroll(float dx, float dy)
    {
        if (_maxScrollX <= 0f || MathF.Abs(dx) <= MathF.Abs(dy))
        {
            base.OnScroll(dx, dy);
            return;
        }

        _scrollX = Math.Clamp(_scrollX - dx * MinTabWidth * 0.5f, 0f, _maxScrollX);
        MarkNeedsLayout();
    }

    private sealed class TabCell(string label, Func<bool> isSelected, Action onTap, TabBar owner)
        : RenderWidget
    {
        private bool _hovered;
        private Size _size;
        private Size _textSize;

        public float DesiredWidth { get; set; }

        public override Size Measure(Constraints c)
        {
            _textSize = TextMeasure.Measure(label, owner.Theme.FontSizeBody, FontWeight.Medium);
            _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
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
        }

        public override void Paint(PaintList paint)
        {
            var theme = owner.Theme;
            var sel = isSelected();

            if (!sel && _hovered)
                paint.AddRect(Bounds, theme.Fill4, Radii.Sm);

            var fs = theme.FontSizeBody;
            var weight = sel ? FontWeight.Medium : FontWeight.Normal;
            var fg = sel ? theme.OnSurface : _hovered ? theme.OnSurface : theme.Hint;

            var bx = Bounds.X + (Bounds.Width - _textSize.Width) / 2f;
            var by = Bounds.Y + (Bounds.Height - _textSize.Height) / 2f + fs * 0.8f;
            paint.AddText(
                label,
                bx,
                by,
                fg,
                fs,
                fontWeight: weight
            );
            // The selected-tab underline is drawn by the parent TabBar so it can slide between tabs.
        }

        public override void OnPointerEnter()
        {
            if (_hovered) return;
            _hovered = true;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            if (!_hovered) return;
            _hovered = false;
            MarkNeedsPaint();
        }

        public override void OnPointerDown(Offset _)
        {
            onTap();
        }
    }
}

/// <summary>
///     Shows the child at index <see cref="SelectedIndex" /> from its <see cref="Children" /> list.
///     Pair with <see cref="TabBar" /> so indices stay in sync.
/// </summary>
public class TabView : RenderWidget, ITickerProvider
{
    private readonly AnimationController _fade;
    private int _prevIndex = -1;
    private int _selectedIndex;
    private Size _size;
    private Ticker? _ticker;

    public TabView()
    {
        _fade = new AnimationController(Motion.Standard, this) { Curve = Curves.EaseOut };
        _fade.OnTick += MarkNeedsPaint;
        _fade.OnCompleted += () =>
        {
            _prevIndex = -1;
            MarkNeedsLayout();
        };
    }

    public List<Widget> Children { get; } = [];

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            // Focus must not stay on a control inside the page being hidden — the page stays
            // attached (no Detach → no NotifyDetached), so keystrokes would keep flowing to an
            // invisible widget. Keyboard tab-switching is unaffected: focus sits on the TabBar,
            // not inside the page, and stays there.
            if (Owner is { FocusedWidget: { } focused } app &&
                _selectedIndex >= 0 && _selectedIndex < Children.Count &&
                IsInside(focused, Children[_selectedIndex]))
                app.ClearFocus();

            // Cross-fade the outgoing page under the incoming one.
            _prevIndex = _selectedIndex;
            _selectedIndex = value;
            _fade.Dismiss();
            _fade.Forward();
            MarkNeedsLayout();
        }
    }

    private bool Transitioning =>
        _prevIndex >= 0 && _prevIndex < Children.Count && _prevIndex != _selectedIndex;

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    private static bool IsInside(Widget w, Widget root)
    {
        for (var n = w; n is not null; n = n.Parent)
            if (ReferenceEquals(n, root))
                return true;
        return false;
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        _fade.AttachTicker(this);
    }

    public override void Detach()
    {
        base.Detach();
        _ticker?.Dispose();
        _ticker = null;
    }

    public override Size Measure(Constraints c)
    {
        var hasChild = _selectedIndex >= 0 && _selectedIndex < Children.Count;

        // Size to the active child on any unbounded axis (e.g. inside a vertical ScrollView, where
        // MaxHeight is infinite). Returning an infinite size up the tree poisons flex layout math
        // (∞ − ∞ → NaN) and crashes paint, so fall back to the child's intrinsic extent instead.
        var childSize = hasChild
            ? Children[_selectedIndex].Measure(
                new Constraints(
                    0,
                    c.MaxWidth,
                    0,
                    c.MaxHeight
                )
            )
            : Size.Zero;

        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : childSize.Width;
        var h = float.IsFinite(c.MaxHeight) ? c.MaxHeight : childSize.Height;
        _size = c.Constrain(new Size(w, h));

        // Re-measure tight so the active child fills the resolved bounds on bounded axes.
        if (hasChild)
            Children[_selectedIndex].Measure(Constraints.Tight(_size.Width, _size.Height));
        // Keep the outgoing page measured to the same bounds while it fades out.
        if (Transitioning)
            Children[_prevIndex].Measure(Constraints.Tight(_size.Width, _size.Height));
        NeedsLayout = false;
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
        if (Transitioning) Children[_prevIndex].Layout(origin);
        if (_selectedIndex >= 0 && _selectedIndex < Children.Count)
            Children[_selectedIndex].Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        // During a tab switch the outgoing page stays put while the incoming one fades in over it.
        if (Transitioning)
        {
            Children[_prevIndex].Paint(paint);
            var t = _fade.Value;
            if (t < 0.999f) paint.PushAlpha(t);
            if (_selectedIndex >= 0 && _selectedIndex < Children.Count)
                Children[_selectedIndex].Paint(paint);
            if (t < 0.999f) paint.PopAlpha();
            return;
        }

        if (_selectedIndex >= 0 && _selectedIndex < Children.Count)
            Children[_selectedIndex].Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        // Route input to the incoming/active page only.
        if (_selectedIndex >= 0 && _selectedIndex < Children.Count)
            return Children[_selectedIndex].HitTest(point);
        return null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return Children;
    }

    /// <summary>
    ///     Only the active page is focus-reachable — hidden pages keep their last laid-out bounds,
    ///     so without this Tab traversal would cycle into invisible controls. The outgoing page of
    ///     a transition is excluded too (it is fading away).
    /// </summary>
    public override IEnumerable<Widget> GetVisibleChildren()
    {
        if (_selectedIndex >= 0 && _selectedIndex < Children.Count)
            yield return Children[_selectedIndex];
    }
}