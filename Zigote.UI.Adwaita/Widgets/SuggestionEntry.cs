using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwSuggestionEntry — an <see cref="AdwEntry" /> with a type-ahead completion list, GTK's
///     entry-completion pattern: a popover of candidates opens under the entry while it is focused
///     and re-filters on every keystroke. Unlike a drop-down it never steals focus — the caret stays
///     in the entry and the list only reacts to the pointer, so typing is never interrupted. The
///     value is committed on an explicit action (picking a row, or Enter) and never per keystroke,
///     so a caller that rebuilds on commit cannot destroy the entry mid-word; whatever was typed is
///     what Enter commits, so a path the completer never suggested still goes through.
/// </summary>
public sealed class AdwSuggestionEntry : AdwEntry
{
    private readonly Action<string> _onCommit;
    private readonly Func<string, IReadOnlyList<(string Value, string Display)>> _suggest;
    private CompletionPopup? _popup;

    /// <param name="value">Initial text.</param>
    /// <param name="suggest">Candidates for the current text, as (value, display name) pairs.</param>
    /// <param name="onCommit">Receives the picked or typed value.</param>
    public AdwSuggestionEntry(string value,
        Func<string, IReadOnlyList<(string Value, string Display)>> suggest,
        Action<string> onCommit)
    {
        _suggest = suggest;
        _onCommit = onCommit;
        Text = value;
        OnSubmitted = Commit;
        Field.OnFocusChange = focused =>
        {
            if (focused) Refresh();
            else Hide();
        };
    }

    protected override void OnTextChanged()
    {
        // Only while the entry is live: the constructor's initial Text assignment and external
        // writes must not pop a completion list at nobody.
        if (Field.Focused) Refresh();
    }

    public override void Detach()
    {
        Hide();
        base.Detach();
    }

    private void Commit(string value)
    {
        Hide();
        _onCommit(value);
    }

    /// <summary>Re-filter and show, hide, or repaint the popup as the candidate count dictates.</summary>
    private void Refresh()
    {
        var items = _suggest(Text);
        if (items.Count == 0)
        {
            Hide();
            return;
        }

        _popup ??= new CompletionPopup(() => Bounds, Commit);
        _popup.SetItems(items);
        if (_popup.Shown) _popup.MarkNeedsPaint();
        else if (Owner is { } owner)
        {
            _popup.Shown = true;
            owner.PushOverlay(_popup);
        }
    }

    private void Hide()
    {
        if (_popup is not { Shown: true } popup) return;
        popup.Shown = false;
        Owner?.PopOverlay(popup);
    }

    /// <summary>
    ///     The completion list: a popover card anchored under the entry. A plain overlay rather than
    ///     an <c>AdwPopoverBase</c> — that one grabs focus the way a menu does, which is exactly
    ///     wrong here, since the caret has to stay in the entry being typed into.
    /// </summary>
    private sealed class CompletionPopup(Func<Rect> anchor, Action<string> onPick) : Widget
    {
        private const float PointerRowH = 28f;
        private const float Pad = 6f;
        private const int MaxRows = 10;

        private int _hover = -1;
        private float _rowH = PointerRowH;
        private Size _screen;
        private ThemeData _theme = ThemeData.Dark;

        public bool Shown { get; set; }
        public List<(string Value, string Display)> Items { get; } = [];

        public void SetItems(IReadOnlyList<(string Value, string Display)> items)
        {
            Items.Clear();
            Items.AddRange(items);
            if (_hover >= Items.Count) _hover = -1;
        }

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            _screen = new Size(c.MaxWidth, c.MaxHeight);
            _rowH = MediaQuery.Of(BuildContext.Current).SizeClass == WindowSizeClass.Compact
                ? MathF.Max(PointerRowH, ControlMetrics.MinTouchTarget)
                : PointerRowH;
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

        /// <summary>
        ///     Rows the card shows. The list does not scroll, so the cap is whichever is smaller:
        ///     the row budget, or what actually fits beside the entry on this screen — a touch-row
        ///     list would otherwise run off the bottom with no way to reach the tail.
        /// </summary>
        private int VisibleRows()
        {
            var a = anchor();
            var room = MathF.Max(a.Y, _screen.Height - a.Bottom) - 8f;
            var fits = _rowH > 0f ? (int)MathF.Floor((room - Pad * 2f) / _rowH) : MaxRows;
            return Math.Max(1, Math.Min(Math.Min(Items.Count, MaxRows), fits));
        }

        private Rect CardRect()
        {
            var a = anchor();
            return OverlayPositioning.Anchored(
                a,
                new Size(MathF.Max(a.Width, 180f), VisibleRows() * _rowH + Pad * 2f),
                _screen,
                OverlaySide.Below,
                4f
            );
        }

        public override void Paint(PaintList paint)
        {
            if (Items.Count == 0) return;
            var card = CardRect();
            paint.AddElevation(card, AdwMetrics.CardRadius, AdwMetrics.PopoverShadow);
            paint.AddRect(card, AdwPalette.For(_theme).PopoverBg, AdwMetrics.CardRadius);
            paint.AddBorder(card, _theme.Border, AdwMetrics.CardRadius);

            var fs = _theme.FontSizeBody;
            var dim = AdwPalette.For(_theme).DimLabel;
            paint.AddClipStart(card);
            for (var i = 0; i < VisibleRows(); i++)
            {
                var row = new Rect(
                    card.X + Pad,
                    card.Y + Pad + i * _rowH,
                    card.Width - Pad * 2f,
                    _rowH
                );
                // GNOME rounds the row highlight and insets it from the card edge; the row keeps
                // the normal label colour under it rather than inverting, as menus do.
                var wash = AdwStyle.RowFill(_theme, i == _hover, false);
                if (wash.A > 0f) paint.AddRect(row, wash, 6f);

                var (val, disp) = Items[i];
                var baseline = row.Y + (_rowH - fs) / 2f + fs * 0.8f;
                paint.AddText(
                    disp,
                    row.X + Pad,
                    baseline,
                    _theme.OnSurface,
                    fs
                );

                // The full value trails the display name, dimmed, when they differ.
                if (string.Equals(val, disp, StringComparison.Ordinal)) continue;
                paint.AddText(
                    val,
                    row.X + Pad + TextMeasure.Width(disp, fs) + Spacing.Sm,
                    baseline,
                    dim,
                    fs - 1f
                );
            }

            paint.AddClipEnd();
        }

        private int RowAt(Offset p)
        {
            var card = CardRect();
            if (!card.Contains(p.X, p.Y)) return -1;
            var idx = (int)((p.Y - card.Y - Pad) / _rowH);
            return idx >= 0 && idx < VisibleRows() ? idx : -1;
        }

        public override Widget? HitTest(Offset point)
        {
            return CardRect().Contains(point.X, point.Y) ? this : null;
        }

        public override void OnPointerMove(Offset point)
        {
            var idx = RowAt(point);
            if (idx == _hover) return;
            _hover = idx;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            if (_hover == -1) return;
            _hover = -1;
            MarkNeedsPaint();
        }

        public override void OnPointerDown(Offset point)
        {
            var idx = RowAt(point);
            if (idx >= 0) onPick(Items[idx].Value);
        }

        public override MouseCursor? GetCursor(Offset point)
        {
            return RowAt(point) >= 0 ? MouseCursor.Pointer : null;
        }
    }
}
