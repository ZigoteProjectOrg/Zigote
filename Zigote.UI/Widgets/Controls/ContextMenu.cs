using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Overlays;
using AppInstance = Zigote.UI.Host.App;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     One menu row, shared by the in-window <see cref="ContextMenu" />/<c>MenuBar</c> and the
///     native macOS bar. <paramref name="Icon" /> is an <see cref="Zigote.UI.Theme.Icons" /> glyph
///     (in-window rendering); <paramref name="SystemImage" /> is an SF Symbol name (native macOS) —
///     set both for an icon on every backend. <paramref name="Checked" /> is tri-state: null means
///     not checkable, true paints the leading checkmark. <paramref name="Enabled" /> greys the row
///     out even when <paramref name="OnSelect" /> is set (an item with no action is disabled
///     regardless).
/// </summary>
public sealed record ContextMenuItem(
    string Label,
    Action? OnSelect,
    bool Separator = false,
    IReadOnlyList<ContextMenuItem>? Children = null,
    string? Shortcut = null,
    string? Icon = null,
    string? SystemImage = null,
    bool? Checked = null,
    bool Enabled = true)
{
    /// <summary>A row is actionable when it has an action (or a submenu) and isn't disabled.</summary>
    public bool IsEnabled => Enabled && (OnSelect is not null || Children is { Count: > 0 });
}

/// <summary>
///     A flat, macOS-style right-click context menu. A <see cref="ThemeData.Surface" /> popover floats
///     on a soft Z2 shadow with a hairline <see cref="ThemeData.Separator" /> border; the hovered row
///     is
///     painted with the accent <see cref="ThemeData.Selection" /> fill and
///     <see cref="ThemeData.OnPrimary" />
///     text. Width is measured from the widest item. Appears at the click position and dismisses on
///     item
///     selection or click outside. Items with Children open a nested submenu on hover.
/// </summary>
public sealed class ContextMenu : RenderWidget, ITickerProvider
{
    private const float SeparatorH = 9f; // compact divider row height

    // Tolerance around the menu/anchor rects for the pointer-exit dismissal, so grazing an edge
    // by a pixel while tracking a row doesn't close the menu.
    private const float ExitSlop = 8f;

    private readonly AppInstance _app;
    private readonly AnimationController _enter;
    private Ticker? _ticker;
    private int _hoveredIdx = -1;
    private float _menuW = ControlMetrics.MenuRowHeight * 8f; // sane fallback before first Measure
    private int _openSubmenuIdx = -1;
    private ContextMenu? _parentMenu; // the menu that opened this one (null for the root)
    private Offset _pos;
    private Size _screen;
    private ContextMenu? _submenu;
    private ThemeData _theme = ThemeData.Dark;

    public ContextMenu(AppInstance? app, params ContextMenuItem[] items)
    {
        _app = app ?? AppInstance.Active ??
            throw new InvalidOperationException("No active App found.");
        Items = [..items];
        _enter = new AnimationController(Motion.Fast, this) { Curve = Curves.EaseOut };
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

    private void PlayEnter()
    {
        _enter.Dismiss();
        _enter.Forward();
    }

    /// <summary>Convenience constructor using <see cref="AppInstance.Active" />.</summary>
    public ContextMenu(params ContextMenuItem[] items)
        : this(null, items)
    {
    }

    public List<ContextMenuItem> Items { get; } = [];

    /// <summary>
    ///     When set, the menu dismisses as soon as the pointer leaves the menu chain and this rect
    ///     (the control that opened it — e.g. a <c>MenuBar</c> title button). Menu-bar dropdown
    ///     behavior; leave null for the context-menu default (dismiss on click-outside only).
    /// </summary>
    public Rect? PointerExitScope { get; set; }

    // Resolved at ShowAt(): with secondary OS windows, the window presenting the menu is the one
    // whose dispatch is running (App.Active), which may differ from the window active at construction.
    private AppInstance? _host;

    public void ShowAt(Offset position)
    {
        _pos = position;
        _hoveredIdx = -1;
        _host = AppInstance.Active ?? _app;
        _host.PushOverlay(this);
        PlayEnter();
    }

    public void Dismiss()
    {
        _submenu?.Dismiss();
        _submenu = null;
        _openSubmenuIdx = -1;
        (_host ?? _app).PopOverlay(this);
    }

    /// <summary>Walk up to the root menu (the only one actually pushed as an overlay).</summary>
    private ContextMenu Root()
    {
        var m = this;
        while (m._parentMenu is not null) m = m._parentMenu;
        return m;
    }

    /// <summary>
    ///     Tear down the whole menu chain from any level. App routes a click directly to the leaf
    ///     submenu instance (HitTest returns it), and only the root was ever PushOverlay'd — so a
    ///     submenu's own Dismiss() pops nothing. Dismissing the root cascades to every submenu.
    /// </summary>
    public void DismissAll()
    {
        Root().Dismiss();
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _screen = new Size(c.MaxWidth, c.MaxHeight);
        _menuW = MeasureMenuWidth();
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

    /// <summary>Row height for an item — separators get a slim divider row, everything else is uniform.</summary>
    private static float RowHeight(ContextMenuItem item)
    {
        return item.Separator ? SeparatorH : ControlMetrics.MenuRowHeight;
    }

    /// <summary>
    ///     Leading gutter width for icons/checkmarks — reserved for every row as soon as any item
    ///     in the menu is checkable or has an icon (macOS-style aligned labels), else 0.
    /// </summary>
    private float GutterWidth()
    {
        foreach (var item in Items)
            if (!item.Separator && (item.Icon is not null || item.Checked is not null))
                return _theme.FontSizeBody + Spacing.Sm;
        return 0f;
    }

    /// <summary>Width = gutter + widest (label + gap + shortcut/arrow) across all items, plus insets.</summary>
    private float MeasureMenuWidth()
    {
        var fs = _theme.FontSizeBody;
        var widest = 0f;

        foreach (var item in Items)
        {
            if (item.Separator) continue;

            var w = TextMeasure.Width(item.Label, fs);

            var hasChildren = item.Children is { Count: > 0 };
            if (hasChildren)
                // Submenu arrow ("▶") rendered at the trailing edge.
                w += Spacing.Lg + TextMeasure.Width("▶", fs * 0.8f);
            else if (item.Shortcut is { Length: > 0 } sc)
                w += Spacing.Lg + TextMeasure.Width(sc, fs);

            if (w > widest) widest = w;
        }

        return widest + GutterWidth() + Spacing.Md * 2f;
    }

    private float TotalHeight()
    {
        var h = 0f;
        foreach (var item in Items) h += RowHeight(item);
        return h;
    }

    private Rect MenuRect()
    {
        var size = new Size(_menuW, TotalHeight());
        var raw = new Rect(
            _pos.X,
            _pos.Y,
            size.Width,
            size.Height
        );
        return OverlayPositioning.Clamp(raw, _screen);
    }

    public override void Paint(PaintList paint)
    {
        var t = _enter.Value;
        var fade = t < 0.999f;
        var rise = (1f - t) * 6f;
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(0f, -rise);

        var mr = MenuRect();

        // Flat popover: soft Z2 lift, opaque surface, hairline border.
        paint.AddElevation(mr, Radii.Lg, Elevation.Z2);
        paint.AddRect(mr, _theme.Surface, Radii.Lg);
        paint.AddBorder(mr, _theme.Separator, Radii.Lg);

        var fs = _theme.FontSizeBody;
        var y = mr.Y;

        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            var rowH = RowHeight(item);
            var row = new Rect(
                mr.X,
                y,
                _menuW,
                rowH
            );
            y += rowH;

            if (item.Separator)
            {
                paint.AddRect(
                    new Rect(
                        row.X + Spacing.Md,
                        row.Y + rowH / 2f - 0.5f,
                        _menuW - Spacing.Md * 2f,
                        1f
                    ),
                    _theme.Separator
                );
                continue;
            }

            var enabled = item.IsEnabled;
            // Disabled rows never take the selection highlight (macOS behavior).
            var hovered = enabled && (_hoveredIdx == i || _openSubmenuIdx == i);
            if (hovered)
                paint.AddRect(row, _theme.Selection, Radii.Xs);

            var hasChildren = item.Children is { Count: > 0 };

            Color fg;
            if (hovered) fg = _theme.OnPrimary;
            else if (!enabled) fg = _theme.Hint;
            else fg = _theme.OnSurface;

            var baseline = row.Y + (rowH - fs) / 2f + fs * 0.8f;

            // Leading gutter: checkmark takes precedence over a decorative icon.
            var gutter = GutterWidth();
            if (gutter > 0f)
            {
                var glyph = item.Checked == true ? Icons.Check : item.Icon;
                if (glyph is not null)
                    Icons.Draw(
                        paint,
                        glyph,
                        new Rect(
                            row.X + Spacing.Md,
                            row.Y + (rowH - fs) / 2f,
                            fs,
                            fs
                        ),
                        fg,
                        fs
                    );
            }

            if (!string.IsNullOrEmpty(item.Label))
                paint.AddText(
                    item.Label,
                    row.X + Spacing.Md + gutter,
                    baseline,
                    fg,
                    fs
                );

            if (hasChildren)
            {
                const string arrow = "▶";
                var arrowFs = fs * 0.8f;
                var arrowW = TextMeasure.Width(arrow, arrowFs);
                paint.AddText(
                    arrow,
                    row.Right - Spacing.Md - arrowW,
                    row.Y + (rowH - arrowFs) / 2f + arrowFs * 0.8f,
                    hovered ? _theme.OnPrimary : _theme.Hint,
                    arrowFs
                );
            }
            else if (item.Shortcut is { Length: > 0 } sc)
            {
                // Right-aligned shortcut, dimmed unless the row is the active selection.
                var scW = TextMeasure.Width(sc, fs);
                paint.AddText(
                    sc,
                    row.Right - Spacing.Md - scW,
                    baseline,
                    hovered ? _theme.OnPrimary : _theme.Hint,
                    fs
                );
            }
        }

        if (rise > 0.01f) paint.PopTranslate();
        if (fade) paint.PopAlpha();

        // Paint nested submenu on top (outside this menu's entrance transform so it isn't double-dimmed).
        _submenu?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        // Forward to submenu first so it gets priority
        if (_submenu is not null)
        {
            var subHit = _submenu.HitTest(point);
            if (subHit is not null) return subHit;
        }

        return
            this; // capture all input over full screen; click-outside dismiss is in OnPointerDown
    }

    /// <summary>Index of the item whose row contains <paramref name="localY" />, or -1.</summary>
    private int RowIndexAt(Rect mr, float localY)
    {
        var y = mr.Y;
        for (var i = 0; i < Items.Count; i++)
        {
            var rowH = RowHeight(Items[i]);
            if (localY >= y && localY < y + rowH) return i;
            y += rowH;
        }

        return -1;
    }

    /// <summary>The pointer is inside this menu or any open submenu (with edge slop).</summary>
    private bool ChainContains(Offset point)
    {
        if (Inflate(MenuRect(), ExitSlop).Contains(point.X, point.Y)) return true;
        return _submenu?.ChainContains(point) ?? false;
    }

    private static Rect Inflate(Rect r, float by)
    {
        return new Rect(
            r.X - by,
            r.Y - by,
            r.Width + by * 2f,
            r.Height + by * 2f
        );
    }

    public override void OnPointerMove(Offset point)
    {
        // Menu-bar dropdown behavior: leaving the menu chain (and the opening control) dismisses.
        // Only the root carries the scope and only it was pushed as an overlay.
        if (_parentMenu is null && PointerExitScope is { } scope &&
            !ChainContains(point) &&
            !Inflate(scope, ExitSlop).Contains(point.X, point.Y))
        {
            DismissAll();
            return;
        }

        // Delegate to submenu if it owns this point
        if (_submenu is not null)
        {
            var mr2 = _submenu.MenuRect();
            if (mr2.Contains(point.X, point.Y))
            {
                _submenu.OnPointerMove(point);
                return;
            }
        }

        var mr = MenuRect();
        if (!mr.Contains(point.X, point.Y))
        {
            if (_hoveredIdx != -1)
            {
                _hoveredIdx = -1;
                MarkNeedsPaint();
            }

            return;
        }

        var idx = RowIndexAt(mr, point.Y);
        if (idx != _hoveredIdx)
        {
            _hoveredIdx = idx;
            MarkNeedsPaint();
        }

        if (idx < 0 || idx >= Items.Count) return;

        var item = Items[idx];
        if (item.Separator || !item.IsEnabled) return;
        var hasChildren = item.Children is { Count: > 0 };

        if (hasChildren && _openSubmenuIdx != idx)
        {
            // Close any previously open submenu
            _submenu?.Dismiss();
            _submenu = null;

            _openSubmenuIdx = idx;

            // Open submenu at the right edge of this menu, aligned to the parent row.
            var rowTop = mr.Y;
            for (var i = 0; i < idx; i++) rowTop += RowHeight(Items[i]);

            _submenu = new ContextMenu(_app, [.. item.Children!]);
            // Submenu shares the screen overlay but we position it manually
            _submenu._parentMenu = this; // owner link so item-select can dismiss the whole chain
            _submenu._screen = _screen;
            _submenu._theme = _theme;
            _submenu._menuW = _submenu.MeasureMenuWidth();
            _submenu._pos = new Offset(mr.Right, rowTop);
            _submenu.PlayEnter();
            MarkNeedsPaint();
        }
        else if (!hasChildren && _openSubmenuIdx >= 0)
        {
            _submenu?.Dismiss();
            _submenu = null;
            _openSubmenuIdx = -1;
            MarkNeedsPaint();
        }
    }

    public override void OnPointerDown(Offset point)
    {
        // Delegate click to submenu if it's open and contains the point
        if (_submenu is not null)
        {
            var subMr = _submenu.MenuRect();
            if (subMr.Contains(point.X, point.Y))
            {
                _submenu.OnPointerDown(point);
                return;
            }
        }

        var mr = MenuRect();
        if (!mr.Contains(point.X, point.Y))
        {
            DismissAll();
            return;
        }

        var idx = RowIndexAt(mr, point.Y);
        if (idx >= 0 && idx < Items.Count)
        {
            var item = Items[idx];
            if (item.Separator || !item.IsEnabled) return;
            // Submenu parent items open on hover; clicking them does nothing
            if (item.Children is { Count: > 0 }) return;

            if (item.OnSelect is { } sel)
            {
                // Close the entire chain (root overlay) BEFORE running the action — the action may
                // push its own overlays/menus, which must land on a clean overlay list.
                DismissAll();
                sel();
            }
        }
    }
}