using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     The overlay skeleton every Adwaita popover shares: an <see cref="AdwColors.PopoverBg" />
///     card at radius 15 (hairline border, Z2 lift) anchored below/above its trigger, entering
///     with a ~150ms ease-out fade + 6px rise and leaving by reversing the same motion. It
///     captures all input while shown — click-outside or Esc dismisses — and takes focus, the way
///     a GTK popover grabs it, so Up/Down/Enter reach the list and Tab cannot walk into the page
///     behind it. The capture is released during the exit fade so no row can be picked twice.
///     Subclasses supply their own measurement, row painting and hit mapping.
/// </summary>
internal abstract class AdwPopoverBase : Widget, IDismissableOverlay
{
    private readonly Rect _anchor;
    private readonly App _app;
    private readonly AnimationController _enter;

    protected int Hovered = -1;
    protected float PopupH;
    protected float PopupW;
    protected int PressedRow = -1;
    protected float RowH = AdwMetrics.MenuRowHeight;
    protected Size Screen;
    protected ThemeData Theme = ThemeData.Dark;

    private bool _closing;

    protected AdwPopoverBase(App app, Rect anchor)
    {
        _app = app;
        _anchor = anchor;
        _enter = new AnimationController(durationSeconds: 0.15f, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _enter.OnTick += MarkNeedsPaint;
        _enter.OnDismissed += FinishDismiss;
    }

    // Auto-focused when pushed, so Up/Down/Enter arrive at OnKey (Esc stays with the app's
    // dismiss chain); HandlesDirectionalKeys keeps arrows from moving window focus instead.
    public override bool Focusable => true;
    public override bool HandlesDirectionalKeys => true;

    /// <summary>Row the highlight starts on, so Enter can re-pick the current choice.</summary>
    protected virtual int InitialHighlight => -1;

    public bool RequestDismiss()
    {
        Dismiss();
        return true;
    }

    // ── Ticker plumbing (Toast pattern: rebind on Attach, dispose on Detach) ───


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount() => _enter.AttachTicker(this);


    public void Show()
    {
        Hovered = InitialHighlight;
        _app.PushOverlay(this);
        _enter.Dismiss();
        _enter.Forward();
    }

    // Dialog.cs pattern: play the enter animation backwards, pop when it reaches zero.
    protected void Dismiss()
    {
        if (_closing) return;
        if (_enter.Progress <= 0f)
        {
            // Dismissed before the first frame: nothing to animate out.
            FinishDismiss();
            return;
        }

        _closing = true;
        _enter.Reverse();
    }

    private void FinishDismiss()
    {
        _closing = false;
        _app.PopOverlay(this);
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: Screen.Width,
            height: Screen.Height
        );
    }

    protected Rect PopupRect() => OverlayPositioning.Anchored(
        anchor: _anchor,
        size: new Size(width: PopupW, height: PopupH),
        screen: Screen
    );

    public override void Paint(PaintList paint)
    {
        float t = _enter.Value;
        bool fade = t < 0.999f;
        float rise = (1f - t) * 6f;
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(dx: 0f, dy: -rise);

        var mr = PopupRect();
        // `popover > contents { border-radius: $popover_radius }` — 15px, six more than a control
        // and three more than a card, which is what tells a floating surface from an inline one.
        paint.AddElevation(
            bounds: mr,
            radius: AdwMetrics.PopoverRadius,
            style: AdwMetrics.PopoverShadow
        );
        paint.AddRect(
            bounds: mr,
            color: AdwPalette.For(Theme).PopoverBg,
            radius: AdwMetrics.PopoverRadius
        );
        paint.AddBorder(bounds: mr, color: Theme.Border, radius: AdwMetrics.PopoverRadius);
        PaintRows(paint: paint, mr: mr);

        if (rise > 0.01f) paint.PopTranslate();
        if (fade) paint.PopAlpha();
    }

    /// <summary>Paint the contents of the card <paramref name="mr" />, clipping as it needs to.</summary>
    protected abstract void PaintRows(PaintList paint, Rect mr);

    /// <summary>Index of the activatable row at <paramref name="y" />, or -1 for none.</summary>
    protected abstract int RowAt(Rect mr, float y);

    /// <summary>
    ///     Commit row <paramref name="idx" />. Implementations start the close before invoking, so
    ///     the action fires now and only the fade is deferred.
    /// </summary>
    protected abstract void Activate(int idx);

    // Capture all input over the full screen; click-outside dismiss lives in OnPointerDown.
    // While the exit animation plays, release the capture so no row can be re-picked.
    public override Widget? HitTest(Offset point) =>
        !_closing && Bounds.Contains(px: point.X, py: point.Y) ? this : null;

    public override void OnPointerMove(Offset point)
    {
        var mr = PopupRect();
        int idx = mr.Contains(px: point.X, py: point.Y) ? RowAt(mr: mr, y: point.Y) : -1;
        if (idx == Hovered) return;
        Hovered = idx;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (Hovered == -1) return;
        Hovered = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        int pressed = PressedRow;
        PressedRow = -1;
        if (pressed < 0) return;

        MarkNeedsPaint();
        var mr = PopupRect();
        if (!mr.Contains(px: point.X, py: point.Y) || RowAt(mr: mr, y: point.Y) != pressed)
        {
            Hovered = -1;
            return;
        }

        Activate(pressed);
    }

    public override void OnPointerCancel()
    {
        if (PressedRow < 0 && Hovered < 0) return;
        PressedRow = -1;
        Hovered = -1;
        MarkNeedsPaint();
    }
}

/// <summary>
///     The shared Adwaita popover list behind <see cref="AdwSplitButton" /> menus,
///     <see cref="AdwDropDown" /> and <see cref="AdwComboRow" />: <see cref="AdwPopoverBase" />'s
///     card filled with 32px rows that carry a hover wash and — when <c>showCheck</c> — an accent
///     check on the selected row. Scrolls when tall (wheel + touch, commit-on-lift on compact) and
///     navigates with Up/Down/Enter, opening with the current choice already highlighted.
/// </summary>
internal sealed class AdwPopover : AdwPopoverBase
{
    private const float CheckW = 28f; // left gutter reserved for the selected ✓
    private const float TextPad = 10f; // row text inset when there is no check gutter
    private const float Pad = 6f; // inner padding around the row list
    private const float MaxPopupH = 360f;

    private readonly IReadOnlyList<string> _items;
    private readonly float _minWidth;
    private readonly Action<int> _onPick;
    private readonly bool _showCheck;

    private bool _compact;
    private float _maxScroll;
    private float _scrollY;
    private bool _scrolledToSelection;

    public AdwPopover(
        App app,
        IReadOnlyList<string> items,
        Rect anchor,
        Action<int> onPick,
        int selected = -1,
        bool showCheck = false,
        float minWidth = 0f)
        : base(app: app, anchor: anchor)
    {
        _items = items;
        _onPick = onPick;
        InitialHighlight = selected;
        _showCheck = showCheck;
        _minWidth = minWidth;
    }

    // GTK opens a drop-down with the current choice highlighted, so Enter re-picks it.
    protected override int InitialHighlight { get; }

    public override Size Measure(Constraints c)
    {
        Theme = ThemeProvider.Of(BuildContext.Current);
        Screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
        _compact = MediaQuery.Of(BuildContext.Current).SizeClass == WindowSizeClass.Compact;
        RowH = _compact
            ? MathF.Max(x: AdwMetrics.MenuRowHeight, y: ControlMetrics.MinTouchTarget)
            : AdwMetrics.MenuRowHeight;

        float fs = Theme.FontSizeBody;
        float widest = 0f;
        for (int i = 0; i < _items.Count; i++)
            widest = MathF.Max(x: widest, y: TextMeasure.Width(text: _items[i], fontSize: fs));
        float gutter = _showCheck ? CheckW : TextPad;
        PopupW = MathF.Max(x: _minWidth, y: widest + gutter + TextPad + (Pad * 2f));
        PopupW = MathF.Min(x: PopupW, y: MathF.Max(x: 120f, y: Screen.Width - Spacing.Lg));

        float content = (_items.Count * RowH) + (Pad * 2f);
        float cap = MathF.Min(x: MaxPopupH, y: Screen.Height - 16f);
        PopupH = MathF.Min(x: content, y: cap);
        _maxScroll = MathF.Max(x: 0f, y: content - PopupH);

        // First measure: centre the selected row when the list overflows.
        if (_scrolledToSelection) return Screen;
        _scrolledToSelection = true;
        if (_maxScroll > 0f && InitialHighlight >= 0)
        {
            _scrollY = Math.Clamp(
                value: (InitialHighlight * RowH) - (PopupH * 0.5f) + (RowH * 0.5f),
                min: 0f,
                max: _maxScroll
            );
        }

        return Screen;
    }

    protected override void PaintRows(PaintList paint, Rect mr)
    {
        float fs = Theme.FontSizeBody;
        float gutter = _showCheck ? CheckW : TextPad;
        paint.AddClipStart(mr);
        for (int i = 0; i < _items.Count; i++)
        {
            float rowY = mr.Y + Pad + (i * RowH) - _scrollY;
            if (rowY + RowH <= mr.Y || rowY >= mr.Bottom) continue;

            var row = new Rect(
                x: mr.X + Pad,
                y: rowY,
                width: PopupW - (Pad * 2f),
                height: RowH
            );
            // `popover.menu list > row { border-radius: $menu_radius }` on the $selected ladder.
            var wash = AdwStyle.MenuRowFill(
                theme: Theme,
                hovered: i == Hovered,
                pressed: i == PressedRow
            );
            if (wash.A > 0f) paint.AddRect(bounds: row, color: wash, radius: AdwMetrics.MenuRadius);

            float baseline = row.Y + ((RowH - fs) / 2f) + (fs * 0.8f);
            if (_showCheck && i == InitialHighlight)
            {
                Icons.DrawAt(
                    paint: paint,
                    glyph: Icons.Check,
                    x: row.X + Spacing.Xs,
                    baselineY: baseline,
                    color: Theme.PrimaryDark,
                    size: fs
                );
            }

            paint.AddText(
                text: _items[i],
                baselineX: row.X + gutter,
                baselineY: baseline,
                color: Theme.OnBackground,
                fontSize: fs
            );
        }

        paint.AddClipEnd();

        // Slim scrollbar thumb when the list overflows.
        if (_maxScroll <= 0f) return;
        float contentH = (_items.Count * RowH) + (Pad * 2f);
        float thumb = MathF.Max(x: 24f, y: mr.Height * (PopupH / contentH));
        float thumbY = mr.Y + ((mr.Height - thumb) * (_scrollY / _maxScroll));
        paint.AddRect(
            bounds: new Rect(
                x: mr.Right - 5f,
                y: thumbY,
                width: 3f,
                height: thumb
            ),
            color: Theme.OnSurface.WithAlpha(0.25f),
            radius: 1.5f
        );
    }

    protected override int RowAt(Rect mr, float y)
    {
        if (y < mr.Y + Pad || y >= mr.Bottom - Pad) return -1;
        int idx = (int)((y - mr.Y - Pad + _scrollY) / RowH);
        return idx >= 0 && idx < _items.Count ? idx : -1;
    }

    protected override void Activate(int idx)
    {
        Dismiss();
        _onPick(idx);
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

        // On a phone the finger may be starting a scroll drag: commit on lift instead.
        if (_compact)
        {
            PressedRow = Hovered = idx;
            MarkNeedsPaint();
            return;
        }

        Activate(idx);
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Down:
                MoveHighlight(+1);
                break;
            case KeyCode.Up:
                MoveHighlight(-1);
                break;
            case KeyCode.Enter or KeyCode.Space:
                if (Hovered >= 0 && Hovered < _items.Count) Activate(Hovered);
                break;
        }
    }

    private void MoveHighlight(int dir)
    {
        int n = _items.Count;
        if (n == 0) return;
        Hovered = ((Hovered < 0 ? dir > 0 ? -1 : 0 : Hovered) + dir + n) % n;

        // Follow the highlight with the viewport, the way GTK keeps the cursor row visible.
        float top = Hovered * RowH;
        _scrollY = MathF.Max(x: _scrollY, y: top + RowH - (PopupH - (Pad * 2f)));
        _scrollY = Math.Clamp(value: MathF.Min(x: _scrollY, y: top), min: 0f, max: _maxScroll);
        MarkNeedsPaint();
    }

    public override void OnScroll(float dx, float dy)
    {
        if (_maxScroll <= 0f) return;
        _scrollY = Math.Clamp(value: _scrollY - (dy * RowH * 3f), min: 0f, max: _maxScroll);
        MarkNeedsPaint();
    }

    public override bool CanTouchScroll(bool vertical) => vertical && _maxScroll > 0f;

    public override void OnTouchScroll(float dx, float dy)
    {
        if (_maxScroll <= 0f) return;
        _scrollY = Math.Clamp(value: _scrollY - dy, min: 0f, max: _maxScroll);
        MarkNeedsPaint();
    }
}
