using Zigote.UI.TextShaping;
using AppInstance = Zigote.UI.Host.App;

namespace Zigote.UI.Material;

/// <summary>
///     A text field with a type-ahead suggestion popup. Wraps a <see cref="TextField" /> for editing
///     and
///     shows a filtered, scrollable list of candidates (anchored below the field) while focused. The
///     value is committed only on an explicit action — picking a suggestion or pressing Enter — never
///     per
///     keystroke, so the field isn't destroyed mid-type by a caller that rebuilds on commit. Manual
///     entry
///     is preserved: pressing Enter commits whatever is typed. The popup is non-modal; clicking
///     elsewhere
///     blurs the field, which dismisses it.
/// </summary>
public sealed class AutoSuggestField : Widget
{
    private readonly AppInstance _app;
    private readonly TextField _field;
    private readonly Action<string> _onCommit;
    private readonly Func<string, IReadOnlyList<(string Value, string Display)>> _suggest;
    private SuggestionPopup? _popup;
    private Size _size;

    public AutoSuggestField(AppInstance app, string value,
        Func<string, IReadOnlyList<(string Value, string Display)>> suggest,
        Action<string> onCommit, string hint = "")
    {
        _app = app;
        _suggest = suggest;
        _onCommit = onCommit;
        _field = new TextField(decoration: new InputDecoration(hint)) {
            Text = value,
            OnChanged = _ => Refresh(),
            OnSubmitted = Commit,
            OnFocusChange = focused =>
            {
                if (focused) Show();
                else Hide();
            },
        };
    }

    public float Height
    {
        get => _field.Height;
        set => _field.Height = value;
    }

    public float MinWidth
    {
        get => _field.MinWidth;
        set => _field.MinWidth = value;
    }

    public override bool Focusable => false; // the inner field carries focus

    private void Commit(string value)
    {
        Hide();
        _onCommit(value);
    }

    private void Show()
    {
        _popup ??= new SuggestionPopup(anchor: () => _field.Bounds, onPick: Commit);
        _popup.SetItems(_suggest(_field.Text));
        if (_popup.Items.Count == 0)
        {
            Hide();
            return;
        }

        if (!_popup.Shown)
        {
            _popup.Shown = true;
            _app.PushOverlay(_popup);
        }
    }

    private void Hide()
    {
        if (_popup is { Shown: true })
        {
            _popup.Shown = false;
            _app.PopOverlay(_popup);
        }
    }

    private void Refresh()
    {
        if (_popup is null)
        {
            Show();
            return;
        }

        _popup.SetItems(_suggest(_field.Text));
        if (_popup.Items.Count == 0) Hide();
        else if (!_popup.Shown) Show();
        else _popup.MarkNeedsPaint();
    }

    public override Size Measure(Constraints c)
    {
        _size = _field.Measure(c);
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
        _field.Layout(origin);
    }

    public override void Paint(PaintList paint) => _field.Paint(paint);

    public override Widget? HitTest(Offset point) => Bounds.Contains(px: point.X, py: point.Y)
        ? _field.HitTest(point) ?? this
        : null;

    public override IEnumerable<Widget> GetChildren() => [_field];

    public override void Detach()
    {
        Hide();
        base.Detach();
    }

    // ── Suggestion popup overlay ──────────────────────────────────────────────

    private sealed class SuggestionPopup : Widget
    {
        private const float PointerRowH = 22f;
        private const int MaxRows = 10;
        private readonly Func<Rect> _anchor;
        private readonly Action<string> _onPick;
        private int _hover = -1;
        private float _rowH = PointerRowH;
        private Size _screen;
        private ThemeData _theme = ThemeData.Dark;

        public SuggestionPopup(Func<Rect> anchor, Action<string> onPick)
        {
            _anchor = anchor;
            _onPick = onPick;
        }

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
            _screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
            _rowH = TouchMetrics.Pick(PointerRowH);
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

        /// <summary>
        ///     How many rows the popup shows. The list does not scroll, so the cap is whichever is
        ///     smaller: the row budget, or what actually fits beside the anchor on this screen —
        ///     a 44pt-row phone list would otherwise run off the bottom with no way to reach it.
        /// </summary>
        private int VisibleRows()
        {
            var a = _anchor();
            float room = MathF.Max(x: a.Y, y: _screen.Height - a.Bottom) - 8f;
            int fits = _rowH > 0f ? (int)MathF.Floor((room - 4f) / _rowH) : MaxRows;
            return Math.Max(
                val1: 1,
                val2: Math.Min(val1: Math.Min(val1: Items.Count, val2: MaxRows), val2: fits)
            );
        }

        private Rect ListRect()
        {
            var a = _anchor();
            int rows = VisibleRows();
            float h = (rows * _rowH) + 4f;
            float w = MathF.Max(x: a.Width, y: 180f);
            return OverlayPositioning.Anchored(
                anchor: a,
                size: new Size(width: w, height: h),
                screen: _screen,
                side: OverlaySide.Below,
                gap: 2f
            );
        }

        public override void Paint(PaintList paint)
        {
            if (Items.Count == 0) return;
            var lr = ListRect();
            paint.AddElevation(bounds: lr, radius: Radii.Md, style: Elevation.Z2);
            paint.AddRect(bounds: lr, color: _theme.Surface, radius: Radii.Md);
            paint.AddBorder(bounds: lr, color: _theme.Separator, radius: Radii.Md);

            float fs = _theme.FontSizeCaption;
            int rows = VisibleRows();
            paint.AddClipStart(lr);
            for (int i = 0; i < rows; i++)
            {
                float ry = lr.Y + 2f + (i * _rowH);
                var row = new Rect(
                    x: lr.X,
                    y: ry,
                    width: lr.Width,
                    height: _rowH
                );
                if (i == _hover)
                    paint.AddRect(bounds: row, color: _theme.Selection, radius: Radii.Xs);

                (string val, string disp) = Items[i];
                var fg = i == _hover ? _theme.OnPrimary : _theme.OnSurface;
                paint.AddText(
                    text: disp,
                    baselineX: lr.X + Spacing.Sm,
                    baselineY: ry + (_rowH * 0.72f),
                    color: fg,
                    fontSize: fs
                );

                // Dimmed full value tail when it differs from the display name.
                if (!string.Equals(a: val, b: disp, comparisonType: StringComparison.Ordinal))
                {
                    float dispW = TextMeasure.Width(text: disp, fontSize: fs);
                    var tail = (i == _hover ? _theme.OnPrimary : _theme.Hint).WithAlpha(0.55f);
                    paint.AddText(
                        text: val,
                        baselineX: lr.X + Spacing.Sm + dispW + Spacing.Sm,
                        baselineY: ry + (_rowH * 0.72f),
                        color: tail,
                        fontSize: fs - 1f
                    );
                }
            }

            paint.AddClipEnd();
        }

        private int RowAt(Offset p)
        {
            var lr = ListRect();
            if (!lr.Contains(px: p.X, py: p.Y)) return -1;
            int idx = (int)((p.Y - lr.Y - 2f) / _rowH);
            return idx >= 0 && idx < VisibleRows() ? idx : -1;
        }

        public override Widget? HitTest(Offset point) =>
            ListRect().Contains(px: point.X, py: point.Y) ? this : null;

        public override void OnPointerMove(Offset point)
        {
            int idx = RowAt(point);
            if (idx != _hover)
            {
                _hover = idx;
                MarkNeedsPaint();
            }
        }

        public override void OnPointerExit()
        {
            if (_hover == -1) return;
            _hover = -1;
            MarkNeedsPaint();
        }

        public override void OnPointerDown(Offset point)
        {
            int idx = RowAt(point);
            if (idx >= 0) _onPick(Items[idx].Value);
        }
    }
}
