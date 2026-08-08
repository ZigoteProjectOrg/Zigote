using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     The overlay skeleton every Adwaita popover shares: an <see cref="AdwColors.PopoverBg" />
///     card at radius 12 (hairline border, Z2 lift) anchored below/above its trigger, entering
///     with a ~150ms ease-out fade + 6px rise and leaving by reversing the same motion. It
///     captures all input while shown — click-outside or Esc dismisses — and takes focus, the way
///     a GTK popover grabs it, so Up/Down/Enter reach the list and Tab cannot walk into the page
///     behind it. The capture is released during the exit fade so no row can be picked twice.
///     Subclasses supply their own measurement, row painting and hit mapping.
/// </summary>
internal abstract class AdwPopoverBase : RenderWidget, IDismissableOverlay, ITickerProvider
{
    private readonly Rect _anchor;
    private readonly App _app;
    private readonly AnimationController _enter;

    private bool _closing;
    private Ticker? _ticker;

    protected int Hovered = -1;
    protected float PopupH;
    protected float PopupW;
    protected int PressedRow = -1;
    protected float RowH = AdwMetrics.MenuRowHeight;
    protected Size Screen;
    protected ThemeData Theme = ThemeData.Dark;

    protected AdwPopoverBase(App app, Rect anchor)
    {
        _app = app;
        _anchor = anchor;
        _enter = new AnimationController(0.15f, this) { Curve = Curves.EaseOut };
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

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    public override void Attach(App owner, Widget? parent)
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
            origin.X,
            origin.Y,
            Screen.Width,
            Screen.Height
        );
    }

    protected Rect PopupRect()
    {
        return OverlayPositioning.Anchored(_anchor, new Size(PopupW, PopupH), Screen);
    }

    public override void Paint(PaintList paint)
    {
        var t = _enter.Value;
        var fade = t < 0.999f;
        var rise = (1f - t) * 6f;
        if (fade) paint.PushAlpha(t);
        if (rise > 0.01f) paint.PushTranslate(0f, -rise);

        var mr = PopupRect();
        paint.AddElevation(mr, AdwMetrics.CardRadius, Elevation.Z2);
        paint.AddRect(mr, AdwPalette.For(Theme).PopoverBg, AdwMetrics.CardRadius);
        paint.AddBorder(mr, Theme.Border, AdwMetrics.CardRadius);
        PaintRows(paint, mr);

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
    public override Widget? HitTest(Offset point)
    {
        return !_closing && Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public override void OnPointerMove(Offset point)
    {
        var mr = PopupRect();
        var idx = mr.Contains(point.X, point.Y) ? RowAt(mr, point.Y) : -1;
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
        var pressed = PressedRow;
        PressedRow = -1;
        if (pressed < 0) return;

        MarkNeedsPaint();
        var mr = PopupRect();
        if (!mr.Contains(point.X, point.Y) || RowAt(mr, point.Y) != pressed)
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
    private readonly int _selected;
    private readonly bool _showCheck;

    private bool _compact;
    private float _maxScroll;
    private bool _scrolledToSelection;
    private float _scrollY;

    public AdwPopover(
        App app,
        IReadOnlyList<string> items,
        Rect anchor,
        Action<int> onPick,
        int selected = -1,
        bool showCheck = false,
        float minWidth = 0f)
        : base(app, anchor)
    {
        _items = items;
        _onPick = onPick;
        _selected = selected;
        _showCheck = showCheck;
        _minWidth = minWidth;
    }

    // GTK opens a drop-down with the current choice highlighted, so Enter re-picks it.
    protected override int InitialHighlight => _selected;

    public override Size Measure(Constraints c)
    {
        Theme = ThemeProvider.Of(BuildContext.Current);
        Screen = new Size(c.MaxWidth, c.MaxHeight);
        _compact = MediaQuery.Of(BuildContext.Current).SizeClass == WindowSizeClass.Compact;
        RowH = _compact
            ? MathF.Max(AdwMetrics.MenuRowHeight, ControlMetrics.MinTouchTarget)
            : AdwMetrics.MenuRowHeight;

        var fs = Theme.FontSizeBody;
        var widest = 0f;
        for (var i = 0; i < _items.Count; i++)
            widest = MathF.Max(widest, TextMeasure.Width(_items[i], fs));
        var gutter = _showCheck ? CheckW : TextPad;
        PopupW = MathF.Max(_minWidth, widest + gutter + TextPad + Pad * 2f);
        PopupW = MathF.Min(PopupW, MathF.Max(120f, Screen.Width - Spacing.Lg));

        var content = _items.Count * RowH + Pad * 2f;
        var cap = MathF.Min(MaxPopupH, Screen.Height - 16f);
        PopupH = MathF.Min(content, cap);
        _maxScroll = MathF.Max(0f, content - PopupH);

        // First measure: centre the selected row when the list overflows.
        if (_scrolledToSelection) return Screen;
        _scrolledToSelection = true;
        if (_maxScroll > 0f && _selected >= 0)
            _scrollY = Math.Clamp(
                _selected * RowH - PopupH * 0.5f + RowH * 0.5f,
                0f,
                _maxScroll
            );

        return Screen;
    }

    protected override void PaintRows(PaintList paint, Rect mr)
    {
        var fs = Theme.FontSizeBody;
        var gutter = _showCheck ? CheckW : TextPad;
        paint.AddClipStart(mr);
        for (var i = 0; i < _items.Count; i++)
        {
            var rowY = mr.Y + Pad + i * RowH - _scrollY;
            if (rowY + RowH <= mr.Y || rowY >= mr.Bottom) continue;

            var row = new Rect(
                mr.X + Pad,
                rowY,
                PopupW - Pad * 2f,
                RowH
            );
            var wash = AdwStyle.RowFill(Theme, i == Hovered, i == PressedRow);
            if (wash.A > 0f) paint.AddRect(row, wash, 6f);

            var baseline = row.Y + (RowH - fs) / 2f + fs * 0.8f;
            if (_showCheck && i == _selected)
                Icons.DrawAt(
                    paint,
                    Icons.Check,
                    row.X + Spacing.Xs,
                    baseline,
                    Theme.PrimaryDark,
                    fs
                );
            paint.AddText(
                _items[i],
                row.X + gutter,
                baseline,
                Theme.OnBackground,
                fs
            );
        }

        paint.AddClipEnd();

        // Slim scrollbar thumb when the list overflows.
        if (_maxScroll <= 0f) return;
        var contentH = _items.Count * RowH + Pad * 2f;
        var thumb = MathF.Max(24f, mr.Height * (PopupH / contentH));
        var thumbY = mr.Y + (mr.Height - thumb) * (_scrollY / _maxScroll);
        paint.AddRect(
            new Rect(
                mr.Right - 5f,
                thumbY,
                3f,
                thumb
            ),
            Theme.OnSurface.WithAlpha(0.25f),
            1.5f
        );
    }

    protected override int RowAt(Rect mr, float y)
    {
        if (y < mr.Y + Pad || y >= mr.Bottom - Pad) return -1;
        var idx = (int)((y - mr.Y - Pad + _scrollY) / RowH);
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
        if (!mr.Contains(point.X, point.Y))
        {
            Dismiss();
            return;
        }

        var idx = RowAt(mr, point.Y);
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
        var n = _items.Count;
        if (n == 0) return;
        Hovered = ((Hovered < 0 ? dir > 0 ? -1 : 0 : Hovered) + dir + n) % n;

        // Follow the highlight with the viewport, the way GTK keeps the cursor row visible.
        var top = Hovered * RowH;
        _scrollY = MathF.Max(_scrollY, top + RowH - (PopupH - Pad * 2f));
        _scrollY = Math.Clamp(MathF.Min(_scrollY, top), 0f, _maxScroll);
        MarkNeedsPaint();
    }

    public override void OnScroll(float dx, float dy)
    {
        if (_maxScroll <= 0f) return;
        _scrollY = Math.Clamp(_scrollY - dy * RowH * 3f, 0f, _maxScroll);
        MarkNeedsPaint();
    }

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