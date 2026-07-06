using Zigote.UI.TextShaping;
using AppInstance = Zigote.UI.Host.App;
using Zigote.UI.Host;

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
            OnSubmit = Commit,
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
        _popup ??= new SuggestionPopup(() => _field.Bounds, Commit);
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
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _field.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _field.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? _field.HitTest(point) ?? this : null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_field];
    }

    public override void Detach()
    {
        Hide();
        base.Detach();
    }

    // ── Suggestion popup overlay ──────────────────────────────────────────────

    private sealed class SuggestionPopup : Widget
    {
        private const float RowH = 22f;
        private const int MaxRows = 10;
        private readonly Func<Rect> _anchor;
        private readonly Action<string> _onPick;
        private int _hover = -1;
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
            _screen = new Size(c.MaxWidth, c.MaxHeight);
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

        private Rect ListRect()
        {
            var a = _anchor();
            var rows = Math.Min(Items.Count, MaxRows);
            var h = rows * RowH + 4f;
            var w = MathF.Max(a.Width, 180f);
            return OverlayPositioning.Anchored(
                a,
                new Size(w, h),
                _screen,
                OverlaySide.Below,
                2f
            );
        }

        public override void Paint(PaintList paint)
        {
            if (Items.Count == 0) return;
            var lr = ListRect();
            paint.AddElevation(lr, Radii.Md, Elevation.Z2);
            paint.AddRect(lr, _theme.Surface, Radii.Md);
            paint.AddBorder(lr, _theme.Separator, Radii.Md);

            var fs = _theme.FontSizeCaption;
            var rows = Math.Min(Items.Count, MaxRows);
            paint.AddClipStart(lr);
            for (var i = 0; i < rows; i++)
            {
                var ry = lr.Y + 2f + i * RowH;
                var row = new Rect(
                    lr.X,
                    ry,
                    lr.Width,
                    RowH
                );
                if (i == _hover) paint.AddRect(row, _theme.Selection, Radii.Xs);

                var (val, disp) = Items[i];
                var fg = i == _hover ? _theme.OnPrimary : _theme.OnSurface;
                paint.AddText(
                    disp,
                    lr.X + Spacing.Sm,
                    ry + RowH * 0.72f,
                    fg,
                    fs
                );

                // Dimmed full value tail when it differs from the display name.
                if (!string.Equals(val, disp, StringComparison.Ordinal))
                {
                    var dispW = TextMeasure.Width(disp, fs);
                    var tail = (i == _hover ? _theme.OnPrimary : _theme.Hint).WithAlpha(0.55f);
                    paint.AddText(
                        val,
                        lr.X + Spacing.Sm + dispW + Spacing.Sm,
                        ry + RowH * 0.72f,
                        tail,
                        fs - 1f
                    );
                }
            }

            paint.AddClipEnd();
        }

        private int RowAt(Offset p)
        {
            var lr = ListRect();
            if (!lr.Contains(p.X, p.Y)) return -1;
            var idx = (int)((p.Y - lr.Y - 2f) / RowH);
            return idx >= 0 && idx < Math.Min(Items.Count, MaxRows) ? idx : -1;
        }

        public override Widget? HitTest(Offset point)
        {
            return ListRect().Contains(point.X, point.Y) ? this : null;
        }

        public override void OnPointerMove(Offset point)
        {
            var idx = RowAt(point);
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
            var idx = RowAt(point);
            if (idx >= 0) _onPick(Items[idx].Value);
        }
    }
}