using Zigote.Core.Animation;
using Zigote.UI.TextShaping;
using AppInstance = Zigote.UI.Host.App;

namespace Zigote.UI.Material;

/// <summary>
///     A dropdown / combo box. Clicking opens a floating, scrollable overlay listing every item (the
///     selected one carries a check), anchored below the control and flipped above when there's no
///     room —
///     the same overlay machinery as <see cref="ContextMenu" />. Set <see cref="CycleOnClick" /> for
///     the
///     legacy behaviour where a click just advances to the next item with no popup.
/// </summary>
public class Dropdown<T>(
    IReadOnlyList<T> items,
    int selectedIndex,
    Func<T, string> displayText,
    Action<int, T>? onChanged = null)
    : Widget
{
    /// <summary>
    ///     Convenience overload for the common <c>T = string</c> case:
    ///     <c>new Dropdown&lt;string&gt;(items, selectedIndex)</c> — the display selector defaults to the
    ///     item itself (<c>s =&gt; s</c>), so callers need not pass an identity function.
    /// </summary>
    public Dropdown(IReadOnlyList<T> items, int selectedIndex, Action<int, T>? onChanged = null)
        : this(
            items,
            selectedIndex,
            x => x?.ToString() ?? string.Empty,
            onChanged
        )
    {
    }

    private bool _hovered;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private IReadOnlyList<T> _items = items;
    private int _selectedIndex = selectedIndex;

    public IReadOnlyList<T> Items
    {
        get => _items;
        set
        {
            if (_items == value) return;
            _items = value;
            MarkNeedsPaint();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            MarkNeedsPaint();
        }
    }

    public Func<T, string> DisplayText { get; set; } = displayText;
    public Action<int, T>? OnChanged { get; set; } = onChanged;
    public float Height { get; set; } = 36f;
    public float MinWidth { get; set; } = 120f;

    /// <summary>
    ///     Font family for the control and popup labels (null = the default UI face). Lets items
    ///     render in a face the default font can't — e.g. a language picker whose entries span
    ///     scripts the active UI font does not cover.
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>When true, a click advances to the next item inline instead of opening the popup.</summary>
    public bool CycleOnClick { get; set; }

    public T? SelectedItem => Items.Count > 0 && SelectedIndex >= 0 && SelectedIndex < Items.Count
        ? Items[SelectedIndex]
        : default;

    public override int DebugStateHash()
    {
        return HashCode.Combine(SelectedIndex, _hovered);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = MathF.Max(MinWidth, c.MaxWidth);
        w = float.IsPositiveInfinity(w) ? MinWidth : w;
        _size = c.Constrain(new Size(w, TouchMetrics.AtLeast(Height)));
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
        var bg = _hovered ? _theme.SurfaceAlt : _theme.Surface;
        var border = _theme.OnSurface.WithAlpha(0.25f);
        paint.AddRect(Bounds, bg, _theme.InputRadius);
        paint.AddBorder(Bounds, border, _theme.InputRadius);

        // Guard against a stale/out-of-range SelectedIndex so painting can never throw — fall back to
        // the first item, or an em-dash when there are none.
        var idx = SelectedIndex >= 0 && SelectedIndex < Items.Count ? SelectedIndex : 0;
        var label = Items.Count > 0 ? DisplayText(Items[idx]) : "—";
        var fs = _theme.FontSizeBody;
        var bx = Bounds.X + 10f;
        var by = Bounds.Y + (Bounds.Height - fs) / 2f + fs * 0.8f;
        paint.AddText(
            label,
            bx,
            by,
            _theme.OnSurface,
            fs,
            fontFamily: FontFamily
        );

        // Arrow ▾ (drawn as a small downward triangle via three rects)
        var ax = Bounds.X + Bounds.Width - 20f;
        var ay = Bounds.Y + Bounds.Height / 2f - 3f;
        paint.AddRect(
            new Rect(
                ax,
                ay,
                10f,
                2f
            ),
            _theme.Hint,
            1f
        );
        paint.AddRect(
            new Rect(
                ax + 2f,
                ay + 2f,
                6f,
                2f
            ),
            _theme.Hint,
            1f
        );
        paint.AddRect(
            new Rect(
                ax + 4f,
                ay + 4f,
                2f,
                2f
            ),
            _theme.Hint,
            1f
        );
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

    public override void OnPointerDown(Offset point)
    {
        if (Items.Count == 0) return;

        if (CycleOnClick)
        {
            SelectedIndex = (SelectedIndex + 1) % Items.Count;
            OnChanged?.Invoke(SelectedIndex, Items[SelectedIndex]);
            MarkNeedsPaint();
            return;
        }

        OpenPopup();
    }

    private void OpenPopup()
    {
        var app = AppInstance.Active;
        if (app is null) return;

        var labels = new string[Items.Count];
        for (var i = 0; i < Items.Count; i++) labels[i] = DisplayText(Items[i]);

        new DropdownPopup(
            app,
            labels,
            SelectedIndex,
            Bounds,
            MinWidth,
            FontFamily,
            i =>
            {
                if (i < 0 || i >= Items.Count) return;
                SelectedIndex = i;
                OnChanged?.Invoke(i, Items[i]);
                MarkNeedsPaint();
            }
        ).Show();
    }
}

/// <summary>
///     The floating list a <see cref="Dropdown{T}" /> opens. A flat, macOS-style popover (soft Z2
///     lift,
///     opaque surface, hairline border) anchored under the control, scrollable when the item list is
///     taller than the available space. Captures all input over the full screen while shown; clicking
///     an
///     item selects it, clicking outside (or Esc) dismisses — mirroring <see cref="ContextMenu" />.
/// </summary>
internal sealed class DropdownPopup : RenderWidget, IDismissableOverlay, ITickerProvider
{
    private const float CheckW = Spacing.Xl; // left gutter reserved for the selected ✓
    private const float MaxPopupH = 360f;

    private readonly Rect _anchor;
    private readonly AppInstance _app;
    private readonly AnimationController _enter;
    private readonly string? _fontFamily;
    private readonly string[] _labels;
    private readonly float _minWidth;
    private readonly Action<int> _onPick;
    private readonly int _selected;

    private bool _compact;
    private int _hovered = -1;
    private float _maxScroll;
    private float _popupH;
    private float _popupW;
    private int _pressedRow = -1;

    /// <summary>Row height: the dense menu rhythm on a pointer, a finger target on a phone.</summary>
    private float _rowH = ControlMetrics.MenuRowHeight;

    private Size _screen;
    private bool _scrolledToSelection;
    private float _scrollY;
    private Ticker? _ticker;
    private ThemeData _theme = ThemeData.Dark;

    public DropdownPopup(AppInstance app, string[] labels, int selected, Rect anchor,
        float minWidth,
        string? fontFamily,
        Action<int> onPick)
    {
        _app = app;
        _labels = labels;
        _selected = selected;
        _anchor = anchor;
        _minWidth = minWidth;
        _fontFamily = fontFamily;
        _onPick = onPick;
        _enter = new AnimationController(Motion.Standard, this) { Curve = Curves.EaseOut };
        _enter.OnTick += MarkNeedsLayout;
    }

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    public override void Attach(AppInstance owner, Widget? parent)
    {
        base.Attach(owner, parent);
        _enter.AttachTicker(this);
    }

    public override void Detach()
    {
        base.Detach();
        _ticker?.Dispose();
        _ticker = null;
    }

    public bool RequestDismiss()
    {
        Dismiss();
        return true;
    }

    public void Show()
    {
        _app.PushOverlay(this);
        _enter.Dismiss();
        _enter.Forward();
    }

    public void Dismiss()
    {
        _app.PopOverlay(this);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _screen = new Size(c.MaxWidth, c.MaxHeight);
        _compact = TouchMetrics.IsCompact;
        _rowH = TouchMetrics.Pick(ControlMetrics.MenuRowHeight);

        var fs = _theme.FontSizeBody;
        var widest = _labels.Aggregate(
            0f,
            (current, l) => MathF.Max(current, TextMeasure.Width(l, fs, fontFamily: _fontFamily))
        );
        _popupW = MathF.Max(_minWidth, MathF.Max(_anchor.Width, widest + CheckW + Spacing.Md));
        // OverlayPositioning only shifts the popup, so a long label would otherwise push rows off
        // a phone screen where they can never be tapped. Rows clip their text at the surface.
        _popupW = MathF.Min(_popupW, MathF.Max(_minWidth, _screen.Width - Spacing.Lg));

        var content = _labels.Length * _rowH;
        var cap = MathF.Min(MaxPopupH, _screen.Height - 16f);
        _popupH = MathF.Min(content, cap);
        _maxScroll = MathF.Max(0f, content - _popupH);

        // First measure: scroll so the selected row is centred (only matters when the list overflows).
        if (_scrolledToSelection) return _screen;
        _scrolledToSelection = true;
        if (_maxScroll > 0f && _selected >= 0)
            _scrollY = Math.Clamp(
                _selected * _rowH - _popupH * 0.5f + _rowH * 0.5f,
                0f,
                _maxScroll
            );

        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _screen.Width,
            _screen.Height
        );
    }

    private Rect PopupRect()
    {
        return OverlayPositioning.Anchored(_anchor, new Size(_popupW, _popupH), _screen);
    }

    public override void Paint(PaintList paint)
    {
        var t = _enter.Value;
        var fade = t < 0.999f;
        var rise = (1f - t) * 6f; // settle downward into place
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(0f, -rise);

        var mr = PopupRect();
        paint.AddElevation(mr, Radii.Md, Elevation.Z2);
        paint.AddRect(mr, _theme.Surface, Radii.Md);
        paint.AddBorder(mr, _theme.Separator, Radii.Md);

        var fs = _theme.FontSizeBody;
        paint.AddClipStart(mr);
        for (var i = 0; i < _labels.Length; i++)
        {
            var rowY = mr.Y + i * _rowH - _scrollY;
            if (rowY + _rowH <= mr.Y || rowY >= mr.Bottom) continue; // outside the visible window

            var row = new Rect(
                mr.X,
                rowY,
                _popupW,
                _rowH
            );
            var hovered = i == _hovered;
            if (hovered) paint.AddRect(row, _theme.Selection, Radii.Xs);

            var fg = hovered ? _theme.OnPrimary : _theme.OnSurface;
            var baseline = row.Y + (_rowH - fs) / 2f + fs * 0.8f;
            if (i == _selected)
                Icons.DrawAt(
                    paint,
                    Icons.Check,
                    row.X + Spacing.Xxs,
                    baseline,
                    hovered ? _theme.OnPrimary : _theme.Primary,
                    fs
                );
            paint.AddText(
                _labels[i],
                row.X + CheckW,
                baseline,
                fg,
                fs,
                fontFamily: _fontFamily
            );
        }

        paint.AddClipEnd();

        // Slim scrollbar thumb when the list overflows.
        if (_maxScroll > 0f)
        {
            var thumb = MathF.Max(24f, mr.Height * (_popupH / (_labels.Length * _rowH)));
            var thumbY = mr.Y + (mr.Height - thumb) * (_scrollY / _maxScroll);
            paint.AddRect(
                new Rect(
                    mr.Right - 5f,
                    thumbY,
                    3f,
                    thumb
                ),
                _theme.OnSurface.WithAlpha(0.25f),
                1.5f
            );
        }

        if (rise > 0.01f) paint.PopTranslate();
        if (fade) paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        // Capture all input over the full screen; click-outside dismiss is in OnPointerDown.
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    private int RowAt(Rect mr, float y)
    {
        if (y < mr.Y || y >= mr.Bottom) return -1;
        var idx = (int)((y - mr.Y + _scrollY) / _rowH);
        return idx >= 0 && idx < _labels.Length ? idx : -1;
    }

    public override void OnPointerMove(Offset point)
    {
        var mr = PopupRect();
        var idx = mr.Contains(point.X, point.Y) ? RowAt(mr, point.Y) : -1;
        if (idx == _hovered) return;
        _hovered = idx;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (_hovered == -1) return;
        _hovered = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        var mr = PopupRect();
        if (!mr.Contains(point.X, point.Y))
        {
            Dismiss();
            return;
        }

        var idx = RowAt(mr, point.Y);
        if (idx < 0) return;

        // A finger that lands on a row must still be able to start a scroll drag, so on a phone the
        // pick commits on lift; a pointer keeps the press-to-select menu behaviour.
        if (_compact)
        {
            _pressedRow = _hovered = idx;
            MarkNeedsPaint();
            return;
        }

        // Pop before invoking — the callback may itself open overlays, which must land on a clean list.
        Dismiss();
        _onPick(idx);
    }

    public override void OnPointerUp(Offset point)
    {
        var pressed = _pressedRow;
        _pressedRow = -1;
        if (pressed < 0) return;

        var mr = PopupRect();
        if (!mr.Contains(point.X, point.Y) || RowAt(mr, point.Y) != pressed)
        {
            _hovered = -1;
            MarkNeedsPaint();
            return;
        }

        Dismiss();
        _onPick(pressed);
    }

    public override void OnPointerCancel()
    {
        if (_pressedRow < 0 && _hovered < 0) return;
        _pressedRow = -1;
        _hovered = -1;
        MarkNeedsPaint();
    }

    public override void OnScroll(float dx, float dy)
    {
        if (_maxScroll <= 0f) return;
        _scrollY = Math.Clamp(_scrollY - dy * _rowH * 3f, 0f, _maxScroll);
        MarkNeedsPaint();
    }

    // A popup taller than its cap is wheel-only otherwise — the 3pt thumb is paint, not a handle.
    public override bool CanTouchScroll(bool vertical)
    {
        return vertical && _maxScroll > 0f;
    }

    public override void OnTouchScroll(float dx, float dy)
    {
        if (_maxScroll <= 0f) return;
        _scrollY = Math.Clamp(_scrollY - dy, 0f, _maxScroll);
        MarkNeedsPaint();
    }
}