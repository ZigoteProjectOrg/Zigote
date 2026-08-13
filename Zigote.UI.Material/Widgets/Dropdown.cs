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
    private bool _hovered;
    private IReadOnlyList<T> _items = items;
    private int _selectedIndex = selectedIndex;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    /// <summary>
    ///     Convenience overload for the common <c>T = string</c> case:
    ///     <c>new Dropdown&lt;string&gt;(items, selectedIndex)</c> — the display selector defaults to the
    ///     item itself (<c>s =&gt; s</c>), so callers need not pass an identity function.
    /// </summary>
    public Dropdown(IReadOnlyList<T> items, int selectedIndex, Action<int, T>? onChanged = null)
        : this(
            items: items,
            selectedIndex: selectedIndex,
            displayText: x => x?.ToString() ?? string.Empty,
            onChanged: onChanged
        ) { }

    public IReadOnlyList<T> Items
    {
        get => _items;
        set => SetPaint(field: ref _items, value: value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetPaint(field: ref _selectedIndex, value: value);
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

    public override int DebugStateHash() =>
        HashCode.Combine(value1: SelectedIndex, value2: _hovered);

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float w = MathF.Max(x: MinWidth, y: c.MaxWidth);
        w = float.IsPositiveInfinity(w) ? MinWidth : w;
        _size = c.Constrain(new Size(width: w, height: TouchMetrics.AtLeast(Height)));
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
    }

    public override void Paint(PaintList paint)
    {
        var bg = _hovered ? _theme.SurfaceAlt : _theme.Surface;
        var border = _theme.OnSurface.WithAlpha(0.25f);
        paint.AddRect(bounds: Bounds, color: bg, radius: _theme.InputRadius);
        paint.AddBorder(bounds: Bounds, color: border, radius: _theme.InputRadius);

        // Guard against a stale/out-of-range SelectedIndex so painting can never throw — fall back to
        // the first item, or an em-dash when there are none.
        int idx = SelectedIndex >= 0 && SelectedIndex < Items.Count ? SelectedIndex : 0;
        string label = Items.Count > 0 ? DisplayText(Items[idx]) : "—";
        float fs = _theme.FontSizeBody;
        float bx = Bounds.X + 10f;
        float by = Bounds.Y + ((Bounds.Height - fs) / 2f) + (fs * 0.8f);
        paint.AddText(
            text: label,
            baselineX: bx,
            baselineY: by,
            color: _theme.OnSurface,
            fontSize: fs,
            fontFamily: FontFamily
        );

        // Arrow ▾ (drawn as a small downward triangle via three rects)
        float ax = Bounds.X + Bounds.Width - 20f;
        float ay = Bounds.Y + (Bounds.Height / 2f) - 3f;
        paint.AddRect(
            bounds: new Rect(
                x: ax,
                y: ay,
                width: 10f,
                height: 2f
            ),
            color: _theme.Hint,
            radius: 1f
        );
        paint.AddRect(
            bounds: new Rect(
                x: ax + 2f,
                y: ay + 2f,
                width: 6f,
                height: 2f
            ),
            color: _theme.Hint,
            radius: 1f
        );
        paint.AddRect(
            bounds: new Rect(
                x: ax + 4f,
                y: ay + 4f,
                width: 2f,
                height: 2f
            ),
            color: _theme.Hint,
            radius: 1f
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
            OnChanged?.Invoke(arg1: SelectedIndex, arg2: Items[SelectedIndex]);
            MarkNeedsPaint();
            return;
        }

        OpenPopup();
    }

    private void OpenPopup()
    {
        var app = AppInstance.Active;
        if (app is null) return;

        string[] labels = new string[Items.Count];
        for (int i = 0; i < Items.Count; i++) labels[i] = DisplayText(Items[i]);

        new DropdownPopup(
            app: app,
            labels: labels,
            selected: SelectedIndex,
            anchor: Bounds,
            minWidth: MinWidth,
            fontFamily: FontFamily,
            onPick: i =>
            {
                if (i < 0 || i >= Items.Count) return;
                SelectedIndex = i;
                OnChanged?.Invoke(arg1: i, arg2: Items[i]);
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
internal sealed class DropdownPopup : Widget, IDismissableOverlay
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
    private float _scrollY;
    private bool _scrolledToSelection;
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
        _enter = new AnimationController(durationSeconds: Motion.Standard, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _enter.OnTick += MarkNeedsLayout;
    }


    public bool RequestDismiss()
    {
        Dismiss();
        return true;
    }


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount() => _enter.AttachTicker(this);

    public void Show()
    {
        _app.PushOverlay(this);
        _enter.Dismiss();
        _enter.Forward();
    }

    public void Dismiss() => _app.PopOverlay(this);

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
        _compact = TouchMetrics.IsCompact;
        _rowH = TouchMetrics.Pick(ControlMetrics.MenuRowHeight);

        float fs = _theme.FontSizeBody;
        float widest = _labels.Aggregate(
            seed: 0f,
            func: (current, l) => MathF.Max(
                x: current,
                y: TextMeasure.Width(text: l, fontSize: fs, fontFamily: _fontFamily)
            )
        );
        _popupW = MathF.Max(
            x: _minWidth,
            y: MathF.Max(x: _anchor.Width, y: widest + CheckW + Spacing.Md)
        );
        // OverlayPositioning only shifts the popup, so a long label would otherwise push rows off
        // a phone screen where they can never be tapped. Rows clip their text at the surface.
        _popupW = MathF.Min(x: _popupW, y: MathF.Max(x: _minWidth, y: _screen.Width - Spacing.Lg));

        float content = _labels.Length * _rowH;
        float cap = MathF.Min(x: MaxPopupH, y: _screen.Height - 16f);
        _popupH = MathF.Min(x: content, y: cap);
        _maxScroll = MathF.Max(x: 0f, y: content - _popupH);

        // First measure: scroll so the selected row is centred (only matters when the list overflows).
        if (_scrolledToSelection) return _screen;
        _scrolledToSelection = true;
        if (_maxScroll > 0f && _selected >= 0)
        {
            _scrollY = Math.Clamp(
                value: (_selected * _rowH) - (_popupH * 0.5f) + (_rowH * 0.5f),
                min: 0f,
                max: _maxScroll
            );
        }

        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _screen.Width,
            height: _screen.Height
        );
    }

    private Rect PopupRect() => OverlayPositioning.Anchored(
        anchor: _anchor,
        size: new Size(width: _popupW, height: _popupH),
        screen: _screen
    );

    public override void Paint(PaintList paint)
    {
        float t = _enter.Value;
        bool fade = t < 0.999f;
        float rise = (1f - t) * 6f; // settle downward into place
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(dx: 0f, dy: -rise);

        var mr = PopupRect();
        paint.AddElevation(bounds: mr, radius: Radii.Md, style: Elevation.Z2);
        paint.AddRect(bounds: mr, color: _theme.Surface, radius: Radii.Md);
        paint.AddBorder(bounds: mr, color: _theme.Separator, radius: Radii.Md);

        float fs = _theme.FontSizeBody;
        paint.AddClipStart(mr);
        for (int i = 0; i < _labels.Length; i++)
        {
            float rowY = mr.Y + (i * _rowH) - _scrollY;
            if (rowY + _rowH <= mr.Y || rowY >= mr.Bottom) continue; // outside the visible window

            var row = new Rect(
                x: mr.X,
                y: rowY,
                width: _popupW,
                height: _rowH
            );
            bool hovered = i == _hovered;
            if (hovered) paint.AddRect(bounds: row, color: _theme.Selection, radius: Radii.Xs);

            var fg = hovered ? _theme.OnPrimary : _theme.OnSurface;
            float baseline = row.Y + ((_rowH - fs) / 2f) + (fs * 0.8f);
            if (i == _selected)
            {
                Icons.DrawAt(
                    paint: paint,
                    glyph: Icons.Check,
                    x: row.X + Spacing.Xxs,
                    baselineY: baseline,
                    color: hovered ? _theme.OnPrimary : _theme.Primary,
                    size: fs
                );
            }

            paint.AddText(
                text: _labels[i],
                baselineX: row.X + CheckW,
                baselineY: baseline,
                color: fg,
                fontSize: fs,
                fontFamily: _fontFamily
            );
        }

        paint.AddClipEnd();

        // Slim scrollbar thumb when the list overflows.
        if (_maxScroll > 0f)
        {
            float thumb = MathF.Max(x: 24f, y: mr.Height * (_popupH / (_labels.Length * _rowH)));
            float thumbY = mr.Y + ((mr.Height - thumb) * (_scrollY / _maxScroll));
            paint.AddRect(
                bounds: new Rect(
                    x: mr.Right - 5f,
                    y: thumbY,
                    width: 3f,
                    height: thumb
                ),
                color: _theme.OnSurface.WithAlpha(0.25f),
                radius: 1.5f
            );
        }

        if (rise > 0.01f) paint.PopTranslate();
        if (fade) paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        // Capture all input over the full screen; click-outside dismiss is in OnPointerDown.
        return Bounds.Contains(px: point.X, py: point.Y) ? this : null;
    }

    private int RowAt(Rect mr, float y)
    {
        if (y < mr.Y || y >= mr.Bottom) return -1;
        int idx = (int)((y - mr.Y + _scrollY) / _rowH);
        return idx >= 0 && idx < _labels.Length ? idx : -1;
    }

    public override void OnPointerMove(Offset point)
    {
        var mr = PopupRect();
        int idx = mr.Contains(px: point.X, py: point.Y) ? RowAt(mr: mr, y: point.Y) : -1;
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
        if (!mr.Contains(px: point.X, py: point.Y))
        {
            Dismiss();
            return;
        }

        int idx = RowAt(mr: mr, y: point.Y);
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
        int pressed = _pressedRow;
        _pressedRow = -1;
        if (pressed < 0) return;

        var mr = PopupRect();
        if (!mr.Contains(px: point.X, py: point.Y) || RowAt(mr: mr, y: point.Y) != pressed)
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
        _scrollY = Math.Clamp(value: _scrollY - (dy * _rowH * 3f), min: 0f, max: _maxScroll);
        MarkNeedsPaint();
    }

    // A popup taller than its cap is wheel-only otherwise — the 3pt thumb is paint, not a handle.
    public override bool CanTouchScroll(bool vertical) => vertical && _maxScroll > 0f;

    public override void OnTouchScroll(float dx, float dy)
    {
        if (_maxScroll <= 0f) return;
        _scrollY = Math.Clamp(value: _scrollY - dy, min: 0f, max: _maxScroll);
        MarkNeedsPaint();
    }
}
