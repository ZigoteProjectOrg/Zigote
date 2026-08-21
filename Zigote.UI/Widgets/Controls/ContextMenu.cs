using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Menu;
using Zigote.UI.Widgets.Overlays;
using AppInstance = Zigote.UI.Host.App;

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
public sealed class ContextMenu : Widget
{
    private const float SeparatorH = 9f; // compact divider row height

    // Tolerance around the menu/anchor rects for the pointer-exit dismissal, so grazing an edge
    // by a pixel while tracking a row doesn't close the menu.
    private const float ExitSlop = 8f;

    private readonly AppInstance _app;
    private readonly AnimationController _enter;

    // Resolved at ShowAt(): with secondary OS windows, the window presenting the menu is the one
    // whose dispatch is running (App.Active), which may differ from the window active at construction.
    private AppInstance? _host;
    private int _hoveredIdx = -1;
    private float _menuW = ControlMetrics.MenuRowHeight * 8f; // sane fallback before first Measure
    private int _openSubmenuIdx = -1;
    private ContextMenu? _parentMenu; // the menu that opened this one (null for the root)
    private Offset _pos;
    private EdgeInsets _safe;
    private Size _screen;
    private ContextMenu? _submenu;
    private ThemeData _theme = ThemeData.Dark;

    public ContextMenu(AppInstance? app, params ContextMenuItem[] items)
    {
        _app = app ?? AppInstance.Active ??
            throw new InvalidOperationException("No active App found.");
        Items = [.. items];
        _enter = new AnimationController(durationSeconds: Motion.Fast, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _enter.OnTick += MarkNeedsLayout;
    }

    /// <summary>Convenience constructor using <see cref="AppInstance.Active" />.</summary>
    public ContextMenu(params ContextMenuItem[] items)
        : this(app: null, items: items) { }

    public List<ContextMenuItem> Items { get; } = [];

    /// <summary>
    ///     When set, the menu dismisses as soon as the pointer leaves the menu chain and this rect
    ///     (the control that opened it — e.g. a <c>MenuBar</c> title button). Menu-bar dropdown
    ///     behavior; leave null for the context-menu default (dismiss on click-outside only).
    /// </summary>
    public Rect? PointerExitScope { get; set; }

    // The ticker CreateTicker hands out is owned by the mount period, so a re-attach just rebinds.
    protected override void OnMount() => _enter.AttachTicker(this);

    private void PlayEnter()
    {
        _enter.Dismiss();
        _enter.Forward();
    }

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
        CloseSubmenu();
        (_host ?? _app).PopOverlay(this);
    }

    /// <summary>Tear down the open submenu (and, through it, the rest of the chain).</summary>
    private void CloseSubmenu()
    {
        if (_submenu is null) return;
        _submenu.CloseSubmenu();
        _submenu.Detach();
        _submenu = null;
        _openSubmenuIdx = -1;
    }

    // Submenus are painted and routed by hand (they are not laid out as children), but they must
    // still be attached: an unmounted widget's ticker is muted, so the entrance animation never
    // advanced and the submenu painted at alpha 0 — open but invisible.
    // ChildOrEmpty, not an iterator: a yield-return enumerable misses Attach/Detach's ICollection
    // fast path, costing an enumerator + ToArray per lifecycle cascade.
    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_submenu);

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
    public void DismissAll() => Root().Dismiss();

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
        _safe = MediaQuery.Of(BuildContext.Current).Padding;
        // Clamping can only shift a surface, never shrink it — so cap the width here or a long
        // label runs off a phone screen and takes its whole row out of reach.
        _menuW = MathF.Min(
            x: MeasureMenuWidth(),
            y: MathF.Max(x: 80f, y: UsableWidth() - Spacing.Lg)
        );
        return _screen;
    }

    /// <summary>Screen width actually available to a menu (safe area excluded).</summary>
    private float UsableWidth() => _screen.Width - _safe.Horizontal;

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _screen.Width,
            height: _screen.Height
        );
    }

    /// <summary>
    ///     Row height for an item — separators get a slim divider row, everything else is uniform.
    ///     The macOS 22 pt row is a precise cursor target but half a fingertip, and the touch slop
    ///     alone is larger than it: at phone width rows take the finger target instead.
    /// </summary>
    private float RowHeight(ContextMenuItem item)
    {
        if (item.Separator) return SeparatorH;
        return WindowSize.ClassFor(UsableWidth()) == WindowSizeClass.Compact
            ? ControlMetrics.MinTouchTarget
            : ControlMetrics.MenuRowHeight;
    }

    /// <summary>
    ///     Leading gutter width for icons/checkmarks — reserved for every row as soon as any item
    ///     in the menu is checkable or has an icon (macOS-style aligned labels), else 0.
    /// </summary>
    private float GutterWidth()
    {
        foreach (var item in Items)
        {
            if (!item.Separator && (item.Icon is not null || item.Checked is not null))
                return _theme.FontSizeBody + Spacing.Sm;
        }

        return 0f;
    }

    /// <summary>
    ///     The shortcut as the local platform writes it — the model is authored once (⌘S) and read as
    ///     "Ctrl+S" off macOS, where the same chord is also what <c>MenuAccelerators</c> binds.
    /// </summary>
    private static string? ShortcutLabel(ContextMenuItem item) =>
        MenuAccelerators.Display(item.Shortcut);

    /// <summary>Width = gutter + widest (label + gap + shortcut/arrow) across all items, plus insets.</summary>
    private float MeasureMenuWidth()
    {
        float fs = _theme.FontSizeBody;
        float widest = 0f;

        foreach (var item in Items)
        {
            if (item.Separator) continue;

            float w = TextMeasure.Width(text: item.Label, fontSize: fs);

            bool hasChildren = item.Children is { Count: > 0 };
            if (hasChildren)
                // Submenu arrow ("▶") rendered at the trailing edge.
                w += Spacing.Lg + TextMeasure.Width(text: "▶", fontSize: fs * 0.8f);
            else if (ShortcutLabel(item) is { Length: > 0 } sc)
                w += Spacing.Lg + TextMeasure.Width(text: sc, fontSize: fs);

            if (w > widest) widest = w;
        }

        return widest + GutterWidth() + (Spacing.Md * 2f);
    }

    private float TotalHeight()
    {
        float h = 0f;
        foreach (var item in Items) h += RowHeight(item);
        return h;
    }

    private Rect MenuRect()
    {
        var size = new Size(width: _menuW, height: TotalHeight());
        var raw = new Rect(
            x: _pos.X,
            y: _pos.Y,
            width: size.Width,
            height: size.Height
        );
        return OverlayPositioning.Clamp(rect: raw, screen: _screen, safe: _safe);
    }

    public override void Paint(PaintList paint)
    {
        float t = _enter.Value;
        bool fade = t < 0.999f;
        float rise = (1f - t) * 6f;
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(dx: 0f, dy: -rise);

        var mr = MenuRect();

        // Flat popover: soft Z2 lift, opaque surface, hairline border.
        paint.AddElevation(bounds: mr, radius: Radii.Lg, style: Elevation.Z2);
        paint.AddRect(bounds: mr, color: _theme.Surface, radius: Radii.Lg);
        paint.AddBorder(bounds: mr, color: _theme.Separator, radius: Radii.Lg);

        // Rows are painted from measured text that can exceed the clamped menu width on a narrow
        // screen; clip so an over-long label ends at the surface instead of over the page.
        paint.AddClipStart(mr);

        float fs = _theme.FontSizeBody;
        float y = mr.Y;

        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            float rowH = RowHeight(item);
            var row = new Rect(
                x: mr.X,
                y: y,
                width: _menuW,
                height: rowH
            );
            y += rowH;

            if (item.Separator)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: row.X + Spacing.Md,
                        y: row.Y + (rowH / 2f) - 0.5f,
                        width: _menuW - (Spacing.Md * 2f),
                        height: 1f
                    ),
                    color: _theme.Separator
                );
                continue;
            }

            bool enabled = item.IsEnabled;
            // Disabled rows never take the selection highlight (macOS behavior).
            bool hovered = enabled && (_hoveredIdx == i || _openSubmenuIdx == i);
            if (hovered)
                paint.AddRect(bounds: row, color: _theme.Selection, radius: Radii.Xs);

            bool hasChildren = item.Children is { Count: > 0 };

            Color fg;
            if (hovered) fg = _theme.OnPrimary;
            else if (!enabled) fg = _theme.Hint;
            else fg = _theme.OnSurface;

            float baseline = row.Y + ((rowH - fs) / 2f) + (fs * 0.8f);

            // Leading gutter: checkmark takes precedence over a decorative icon.
            float gutter = GutterWidth();
            if (gutter > 0f)
            {
                string? glyph = item.Checked == true ? Icons.Check : item.Icon;
                if (glyph is not null)
                {
                    Icons.Draw(
                        paint: paint,
                        glyph: glyph,
                        box: new Rect(
                            x: row.X + Spacing.Md,
                            y: row.Y + ((rowH - fs) / 2f),
                            width: fs,
                            height: fs
                        ),
                        color: fg,
                        size: fs
                    );
                }
            }

            if (!string.IsNullOrEmpty(item.Label))
            {
                paint.AddText(
                    text: item.Label,
                    baselineX: row.X + Spacing.Md + gutter,
                    baselineY: baseline,
                    color: fg,
                    fontSize: fs
                );
            }

            if (hasChildren)
            {
                const string arrow = "▶";
                float arrowFs = fs * 0.8f;
                float arrowW = TextMeasure.Width(text: arrow, fontSize: arrowFs);
                paint.AddText(
                    text: arrow,
                    baselineX: row.Right - Spacing.Md - arrowW,
                    baselineY: row.Y + ((rowH - arrowFs) / 2f) + (arrowFs * 0.8f),
                    color: hovered ? _theme.OnPrimary : _theme.Hint,
                    fontSize: arrowFs
                );
            }
            else if (ShortcutLabel(item) is { Length: > 0 } sc)
            {
                // Right-aligned shortcut, dimmed unless the row is the active selection.
                float scW = TextMeasure.Width(text: sc, fontSize: fs);
                paint.AddText(
                    text: sc,
                    baselineX: row.Right - Spacing.Md - scW,
                    baselineY: baseline,
                    color: hovered ? _theme.OnPrimary : _theme.Hint,
                    fontSize: fs
                );
            }
        }

        paint.AddClipEnd();

        if (rise > 0.01f) paint.PopTranslate();
        if (fade) paint.PopAlpha();

        // Paint nested submenu on top (outside this menu's entrance transform so it isn't double-dimmed).
        _submenu?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
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
        float y = mr.Y;
        for (int i = 0; i < Items.Count; i++)
        {
            float rowH = RowHeight(Items[i]);
            if (localY >= y && localY < y + rowH) return i;
            y += rowH;
        }

        return -1;
    }

    /// <summary>The pointer is inside this menu or any open submenu (with edge slop).</summary>
    private bool ChainContains(Offset point)
    {
        if (Inflate(r: MenuRect(), by: ExitSlop).Contains(px: point.X, py: point.Y)) return true;
        return _submenu?.ChainContains(point) ?? false;
    }

    private static Rect Inflate(Rect r, float by)
    {
        return new Rect(
            x: r.X - by,
            y: r.Y - by,
            width: r.Width + (by * 2f),
            height: r.Height + (by * 2f)
        );
    }

    public override void OnPointerMove(Offset point)
    {
        // Menu-bar dropdown behavior: leaving the menu chain (and the opening control) dismisses.
        // Only the root carries the scope and only it was pushed as an overlay.
        if (_parentMenu is null && PointerExitScope is { } scope &&
            !ChainContains(point) &&
            !Inflate(r: scope, by: ExitSlop).Contains(px: point.X, py: point.Y))
        {
            DismissAll();
            return;
        }

        // Delegate to submenu if it owns this point
        if (_submenu is not null)
        {
            var mr2 = _submenu.MenuRect();
            if (mr2.Contains(px: point.X, py: point.Y))
            {
                _submenu.OnPointerMove(point);
                return;
            }
        }

        var mr = MenuRect();
        if (!mr.Contains(px: point.X, py: point.Y))
        {
            if (_hoveredIdx != -1)
            {
                _hoveredIdx = -1;
                MarkNeedsPaint();
            }

            return;
        }

        int idx = RowIndexAt(mr: mr, localY: point.Y);
        if (idx != _hoveredIdx)
        {
            _hoveredIdx = idx;
            MarkNeedsPaint();
        }

        if (idx < 0 || idx >= Items.Count) return;

        var item = Items[idx];
        if (item.Separator || !item.IsEnabled) return;
        bool hasChildren = item.Children is { Count: > 0 };

        if (hasChildren && _openSubmenuIdx != idx)
            OpenSubmenu(idx: idx, mr: mr);
        else if (!hasChildren && _openSubmenuIdx >= 0)
        {
            CloseSubmenu();
            MarkNeedsPaint();
        }
    }

    /// <summary>
    ///     Replace any open submenu with the one belonging to row <paramref name="idx" />, placed
    ///     beside its parent row.
    /// </summary>
    private void OpenSubmenu(int idx, Rect mr)
    {
        CloseSubmenu();
        _openSubmenuIdx = idx;

        float rowTop = mr.Y;
        for (int i = 0; i < idx; i++) rowTop += RowHeight(Items[i]);
        var row = new Rect(
            x: mr.X,
            y: rowTop,
            width: _menuW,
            height: RowHeight(Items[idx])
        );

        _submenu = new ContextMenu(app: _app, items: [.. Items[idx].Children!]);
        // Submenu shares the screen overlay but we position it manually
        _submenu._parentMenu = this; // owner link so item-select can dismiss the whole chain
        _submenu._screen = _screen;
        _submenu._safe = _safe;
        _submenu._theme = _theme;
        _submenu._menuW = MathF.Min(
            x: _submenu.MeasureMenuWidth(),
            y: MathF.Max(x: 80f, y: UsableWidth() - Spacing.Lg)
        );
        // Anchored rather than "right edge of the parent": on a narrow screen the parent already
        // spans most of the width, and a fixed right placement would clamp back on top of it.
        var placed = OverlayPositioning.Anchored(
            anchor: row,
            size: new Size(width: _submenu._menuW, height: _submenu.TotalHeight()),
            screen: _screen,
            side: OverlaySide.Right,
            gap: 0f,
            safe: _safe
        );
        _submenu._pos = new Offset(x: placed.X, y: placed.Y);
        // Mount before animating: only the root menu is pushed as an overlay, so a submenu that is
        // never attached stays unmounted — and an unmounted widget's ticker is muted.
        if (Owner is not null) _submenu.Attach(owner: Owner, parent: this);
        _submenu.PlayEnter();
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        // Delegate click to submenu if it's open and contains the point
        if (_submenu is not null)
        {
            var subMr = _submenu.MenuRect();
            if (subMr.Contains(px: point.X, py: point.Y))
            {
                _submenu.OnPointerDown(point);
                return;
            }
        }

        var mr = MenuRect();
        if (!mr.Contains(px: point.X, py: point.Y))
        {
            DismissAll();
            return;
        }

        int idx = RowIndexAt(mr: mr, localY: point.Y);
        if (idx >= 0 && idx < Items.Count)
        {
            var item = Items[idx];
            if (item.Separator || !item.IsEnabled) return;
            // Submenu parents open on hover — which fingers do not produce, so a tap has to open
            // them as well. On desktop the row is already open by the time the click lands, so
            // this is a no-op there.
            if (item.Children is { Count: > 0 })
            {
                if (_openSubmenuIdx != idx) OpenSubmenu(idx: idx, mr: mr);
                return;
            }

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
