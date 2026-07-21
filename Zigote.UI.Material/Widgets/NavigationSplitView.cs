using Zigote.Core.Events;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Material;

/// <summary>
///     macOS-style sidebar + detail layout. A flat sidebar of selectable rows on the left,
///     the detail view for the selected item on the right, separated by a draggable
///     <see cref="SplitPane" /> at a ~0.22 ratio. Selecting a row (mouse or Up/Down keys)
///     rebuilds the detail via <c>detailBuilder(index)</c>.
/// </summary>
public sealed class NavigationSplitView : Widget
{
    private readonly Func<int, Widget> _detailBuilder;
    private readonly Sidebar _sidebar;
    private readonly SplitPane _split;
    private Size _size;
    private int _selected;

    public NavigationSplitView(ThemeData theme, IEnumerable<string> items,
        Func<int, Widget> detailBuilder,
        int selected = 0, Action<int>? onChanged = null)
    {
        _detailBuilder = detailBuilder;
        OnChanged = onChanged;

        var labels = items as IReadOnlyList<string> ?? new List<string>(items);
        _selected = labels.Count == 0 ? 0 : Math.Clamp(selected, 0, labels.Count - 1);

        _sidebar = new Sidebar(
            theme,
            labels,
            _selected,
            Select
        );
        _split = new SplitPane(theme, _sidebar, _detailBuilder(_selected)) {
            SplitRatio = 0.22f,
            MinPaneSize = 160f,
        };
    }

    public int SelectedIndex
    {
        get => _selected;
        set => Select(value);
    }

    [Obsolete("Renamed — use SelectedIndex.")]
    public int Selected
    {
        get => SelectedIndex;
        set => SelectedIndex = value;
    }

    public Action<int>? OnChanged { get; set; }

    private void Select(int index)
    {
        if (index == _selected || index < 0 || index >= _sidebar.Count) return;
        _selected = index;
        _sidebar.Selected = index;
        _split.Second = _detailBuilder(index);
        // Re-wire ownership so the freshly built detail subtree is attached to the app.
        if (Owner is not null) _split.Second?.Attach(Owner, _split);
        OnChanged?.Invoke(index);
        MarkNeedsLayout();
    }

    public override Size Measure(Constraints c)
    {
        // Capture the pane's size ourselves: Widget.MeasuredSize is only auto-populated for
        // Stateless/Stateful widgets, so reading _split.MeasuredSize here yields Size.Zero —
        // which left Bounds empty and made the whole split view hit-test-transparent.
        _size = _split.Measure(c);
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
        _split.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _split.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _split.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        yield return _split;
    }
}

/// <summary>
///     Internal vertical list of flat, selectable sidebar rows. Reports selection through
///     <c>onSelect</c>; Up/Down move the selection while the sidebar (or a row) holds focus.
/// </summary>
internal sealed class Sidebar : Widget
{
    private readonly IReadOnlyList<string> _items;
    private readonly Action<int> _onSelect;
    private readonly float[] _rowY;
    private int _hovered = -1;
    private Size _size;
    private ThemeData _theme;

    public Sidebar(ThemeData theme, IReadOnlyList<string> items, int selected, Action<int> onSelect)
    {
        _theme = theme;
        _items = items;
        _onSelect = onSelect;
        Selected = items.Count == 0 ? -1 : Math.Clamp(selected, 0, items.Count - 1);
        _rowY = new float[items.Count];
    }

    public int Count => _items.Count;

    public int Selected { get; set; }

    public override bool Focusable => true;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var h = Spacing.Sm * 2f + _items.Count * (ControlMetrics.RegularHeight + Spacing.Xxs);
        _size = c.Constrain(new Size(c.MaxWidth, MathF.Max(h, c.MinHeight)));
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
        var y = origin.Y + Spacing.Sm;
        for (var i = 0; i < _items.Count; i++)
        {
            _rowY[i] = y;
            y += ControlMetrics.RegularHeight + Spacing.Xxs;
        }
    }

    public override void Paint(PaintList paint)
    {
        // Recessed sidebar background. Uses Background (not SurfaceAlt, which is pure white in the light
        // theme and so was indistinguishable from the surrounding surface) so the source list reads as a
        // distinct panel in both light and dark.
        paint.AddRect(Bounds, _theme.Background);

        var fs = _theme.FontSizeBody;
        var rowW = Bounds.Width - Spacing.Sm * 2f;

        for (var i = 0; i < _items.Count; i++)
        {
            var row = new Rect(
                Bounds.X + Spacing.Sm,
                _rowY[i],
                rowW,
                ControlMetrics.RegularHeight
            );
            var isSelected = i == Selected;

            if (isSelected)
                paint.AddRect(row, _theme.Selection.WithAlpha(0.18f), Radii.Md);
            else if (i == _hovered)
                paint.AddRect(row, _theme.Fill2, Radii.Md);

            var label = _items[i];
            if (!string.IsNullOrEmpty(label))
            {
                var fg = isSelected ? _theme.OnSurface : _theme.Hint;
                var ts = TextMeasure.Measure(label, fs);
                var bx = row.X + Spacing.Md;
                var by = row.Y + (row.Height - ts.Height) / 2f + fs * 0.8f;
                paint.AddText(
                    label,
                    bx,
                    by,
                    fg,
                    fs
                );
            }
        }

        if (Focused)
            paint.AddFocusRing(Bounds, 0f, _theme);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    private int RowAt(Offset point)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var row = new Rect(
                Bounds.X + Spacing.Sm,
                _rowY[i],
                Bounds.Width - Spacing.Sm * 2f,
                ControlMetrics.RegularHeight
            );
            if (row.Contains(point.X, point.Y)) return i;
        }

        return -1;
    }

    public override void OnPointerMove(Offset point)
    {
        var i = RowAt(point);
        if (i == _hovered) return;
        _hovered = i;
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
        var i = RowAt(point);
        if (i < 0 || i == Selected) return;
        _onSelect(i);
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || _items.Count == 0) return;

        switch (scancode)
        {
            case 82: // Up
            {
                var next = Math.Clamp(Selected - 1, 0, _items.Count - 1);
                if (next != Selected) _onSelect(next);
                break;
            }
            case 81: // Down
            {
                var next = Math.Clamp(Selected + 1, 0, _items.Count - 1);
                if (next != Selected) _onSelect(next);
                break;
            }
        }
    }
}