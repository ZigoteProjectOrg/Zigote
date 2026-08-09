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
            if (ReferenceEquals(_items, value)) return;
            _items = value;
            MarkNeedsLayout();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetPaint(ref _selectedIndex, value);
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
        set => SetPaint(ref _enabled, value);
    }

    /// <inheritdoc cref="AdwEntry.Compact" />
    public bool Compact
    {
        get => _compact;
        set => SetLayout(ref _compact, value);
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
        config.AddFlag(SemanticsFlags.Focusable, Enabled)
            .AddFlag(SemanticsFlags.Focused, Focused)
            .AddFlag(SemanticsFlags.Disabled, !Enabled);
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            SelectedIndex,
            _hovered,
            _pressed,
            Focused
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var fs = _theme.FontSizeBody;
        var widest = 0f;
        for (var i = 0; i < Items.Count; i++)
            widest = MathF.Max(widest, TextMeasure.Width(Items[i], fs));
        // label padding + arrow gutter, natural width.
        var w = MathF.Max(80f, widest + Spacing.Md * 2f + 22f);
        _size = c.Constrain(
            new Size(w, Compact ? AdwMetrics.CompactControlHeight : AdwMetrics.ButtonHeight)
        );
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
    }

    public override void Paint(PaintList paint)
    {
        // This widget paints itself, so the whole-control dim an Opacity wrapper would give a
        // composed control is applied here instead.
        if (!Enabled) paint.PushAlpha(AdwStyle.DisabledOpacity);
        var fill = AdwStyle.ButtonFill(
            _theme,
            AdwButtonStyle.Regular,
            _hovered,
            _pressed,
            Enabled
        );
        paint.AddRect(Bounds, fill, AdwMetrics.ControlRadius);

        var fs = _theme.FontSizeBody;
        var baseline = Bounds.Y + (Bounds.Height - fs) / 2f + fs * 0.8f;
        paint.AddClipStart(Bounds);
        paint.AddText(
            SelectedLabel,
            Bounds.X + Spacing.Md,
            baseline,
            _theme.OnBackground,
            fs
        );
        paint.AddClipEnd();

        Icons.Draw(
            paint,
            Icons.DropDown,
            new Rect(
                Bounds.Right - 24f,
                Bounds.Y,
                20f,
                Bounds.Height
            ),
            _theme.OnBackground,
            18f
        );

        if (Focused)
            paint.AddFocusRing(Bounds, AdwMetrics.ControlRadius, _theme);
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
            app,
            Items,
            Bounds,
            i =>
            {
                if (i < 0 || i >= Items.Count) return;
                SelectedIndex = i;
                OnSelected?.Invoke(i);
                MarkNeedsPaint();
            },
            SelectedIndex,
            true,
            Bounds.Width
        ).Show();
    }
}