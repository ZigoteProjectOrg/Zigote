using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwDropDown — the GNOME drop-down: a Regular-fill button showing the selected item and a
///     drop arrow, opening a floating popover list (popover surface, radius 12, Z2 lift) with a
///     check on the selected row (the shared <see cref="AdwPopover" />).
/// </summary>
public sealed class AdwDropDown : Widget
{
    private bool _compact;
    private bool _enabled = true;
    private bool _hovered;
    private IReadOnlyList<string> _items;
    private bool _pressed;
    private int _selectedIndex;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public AdwDropDown(IReadOnlyList<string> items, int selectedIndex = 0,
        Action<int>? onSelected = null)
    {
        _items = items;
        _selectedIndex = selectedIndex;
        OnSelected = onSelected;
    }

    public IReadOnlyList<string> Items
    {
        get => _items;
        set
        {
            if (ReferenceEquals(objA: _items, objB: value)) return;
            _items = value;
            MarkNeedsLayout();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetPaint(field: ref _selectedIndex, value: value);
    }

    public Action<int>? OnSelected { get; set; }

    /// <summary>
    ///     Adwaita disabled controls dim wholesale (50%) and stop responding — including dropping
    ///     out of the focus ring, so Tab skips them. Repaints on change: the dim is painted here,
    ///     so without it flipping this left the control looking enabled until something else
    ///     happened to repaint.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetPaint(field: ref _enabled, value: value);
    }

    /// <inheritdoc cref="AdwEntry.Compact" />
    public bool Compact
    {
        get => _compact;
        set => SetLayout(field: ref _compact, value: value);
    }

    public override bool Focusable => Enabled;

    private string SelectedLabel =>
        Items.Count > 0
            ? Items[SelectedIndex >= 0 && SelectedIndex < Items.Count ? SelectedIndex : 0]
            : "—";

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Button;
        config.Label = SelectedLabel;
        config.Actions = SemanticsAction.Tap | SemanticsAction.Focus;
        config.AddFlag(flag: SemanticsFlags.Focusable, on: Enabled)
            .AddFlag(flag: SemanticsFlags.Focused, on: Focused)
            .AddFlag(flag: SemanticsFlags.Disabled, on: !Enabled);
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: SelectedIndex,
            value2: _hovered,
            value3: _pressed,
            value4: Focused
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float fs = _theme.FontSizeBody;
        float widest = 0f;
        for (int i = 0; i < Items.Count; i++)
            widest = MathF.Max(x: widest, y: TextMeasure.Width(text: Items[i], fontSize: fs));
        // label padding + arrow gutter, natural width.
        float w = MathF.Max(x: 80f, y: widest + (Spacing.Md * 2f) + 22f);
        _size = c.Constrain(
            new Size(
                width: w,
                height: Compact ? AdwMetrics.CompactControlHeight : AdwMetrics.ButtonHeight
            )
        );
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
        // This widget paints itself, so the whole-control dim an Opacity wrapper would give a
        // composed control is applied here instead.
        if (!Enabled) paint.PushAlpha(AdwStyle.DisabledOpacity);
        var fill = AdwStyle.ButtonFill(
            theme: _theme,
            style: AdwButtonStyle.Regular,
            hovered: _hovered,
            pressed: _pressed,
            enabled: Enabled
        );
        paint.AddRect(bounds: Bounds, color: fill, radius: AdwMetrics.ControlRadius);

        float fs = _theme.FontSizeBody;
        float baseline = Bounds.Y + ((Bounds.Height - fs) / 2f) + (fs * 0.8f);
        paint.AddClipStart(Bounds);
        paint.AddText(
            text: SelectedLabel,
            baselineX: Bounds.X + Spacing.Md,
            baselineY: baseline,
            color: _theme.OnBackground,
            fontSize: fs
        );
        paint.AddClipEnd();

        Icons.Draw(
            paint: paint,
            glyph: Icons.DropDown,
            box: new Rect(
                x: Bounds.Right - 24f,
                y: Bounds.Y,
                width: 20f,
                height: Bounds.Height
            ),
            color: _theme.OnBackground,
            size: 18f
        );

        if (Focused)
            paint.AddFocusRing(bounds: Bounds, radius: AdwMetrics.ControlRadius, theme: _theme);
        if (!Enabled) paint.PopAlpha();
    }

    public override void OnPointerEnter()
    {
        if (_hovered) return;
        _hovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (!_hovered && !_pressed) return;
        _hovered = false;
        _pressed = false;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        _pressed = true;
        MarkNeedsPaint();
        OpenPopup();
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_pressed) return;
        _pressed = false;
        MarkNeedsPaint();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        // The popover takes focus once open, so a held key repeats into it rather than stacking
        // another popover on top of this one.
        if (down && (KeyCode)scancode is KeyCode.Space or KeyCode.Enter) OpenPopup();
    }

    private void OpenPopup()
    {
        // The one choke point for pointer and keyboard alike, so a disabled drop-down is inert
        // without a guard on every handler.
        var app = App.Active;
        if (!Enabled || app is null || Items.Count == 0) return;

        new AdwPopover(
            app: app,
            items: Items,
            anchor: Bounds,
            onPick: i =>
            {
                if (i < 0 || i >= Items.Count) return;
                SelectedIndex = i;
                OnSelected?.Invoke(i);
                MarkNeedsPaint();
            },
            selected: SelectedIndex,
            showCheck: true,
            minWidth: Bounds.Width
        ).Show();
    }
}
