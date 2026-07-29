using Zigote.Core.Events;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Material;

/// <summary>Visual tone of a <see cref="ToolbarButton" />.</summary>
public enum ToolbarTone
{
    /// <summary>Quiet — transparent until hover/press (most toolbar actions).</summary>
    Default,

    /// <summary>Accent-filled prominent action (e.g. Play).</summary>
    Primary,

    /// <summary>Destructive — red fill.</summary>
    Danger,

    /// <summary>Positive — green fill.</summary>
    Success,
}

/// <summary>
///     A compact editor-toolbar button: a Material icon (<see cref="Icons" />), an optional label and
///     an optional ▾ dropdown chevron, on a rounded fill that lights up on hover/press from the
///     theme's <see cref="ThemeData.Control" /> tokens. <see cref="Tone" /> selects a quiet icon
///     button (default) or an accent-filled prominent one. Built for grouped toolbar rows.
/// </summary>
public sealed class ToolbarButton : Widget
{
    private const float PadX = 9f;
    private const float Gap = 6f;
    private const float ChevW = 12f;

    private bool _compact;
    private bool _hovered;
    private float _labelW;
    private bool _pressed;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public ToolbarButton(string? icon, Action? onPressed, string? label = null)
    {
        Icon = icon;
        Label = label;
        OnPressed = onPressed;
    }

    /// <summary>Material glyph (an <see cref="Icons" /> constant), or <c>null</c> for a text-only button.</summary>
    public string? Icon { get; set; }

    public string? Label { get; set; }
    public Action? OnPressed { get; set; }
    public ToolbarTone Tone { get; set; } = ToolbarTone.Default;
    public bool Dropdown { get; set; }
    public bool Enabled { get; set; } = true;
    public float IconSize { get; set; } = 16f;

    public override bool Focusable => true;

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            Icon,
            Label,
            _hovered,
            _pressed,
            Enabled,
            Tone
        );
    }

    private float ContentWidth()
    {
        var w = 0f;
        var has = false;
        if (Icon != null)
        {
            w += IconSize;
            has = true;
        }

        if (_labelW > 0f)
        {
            if (has) w += Gap;
            w += _labelW;
            has = true;
        }

        if (Dropdown)
        {
            if (has) w += 2f;
            w += ChevW;
        }

        return w;
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _compact = TouchMetrics.IsCompact;
        var fs = _theme.FontSizeCaption;
        _labelW = string.IsNullOrEmpty(Label)
            ? 0f
            : TextMeasure.Width(Label, fs, FontWeight.Medium);
        // The bar itself is already 44 tall; its buttons were a fixed 28, so on a phone nothing in
        // a touch-sized toolbar was actually touch-sized.
        var h = TouchMetrics.Pick(ControlMetrics.RegularHeight);
        _size = c.Constrain(new Size(MathF.Max(ContentWidth() + PadX * 2f, h), h));
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
        var radius = Radii.Sm;
        var state = StateStyle.StateOf(_hovered, _pressed, !Enabled);

        // Fill: accent tones are always filled; the quiet default only fills on hover/press.
        Color? fill = Tone switch {
            ToolbarTone.Primary => StateStyle.Tint(_theme.Accent, state),
            ToolbarTone.Danger => StateStyle.Tint(_theme.Danger, state),
            ToolbarTone.Success => StateStyle.Tint(_theme.Success, state),
            // The quiet tone is transparent at rest and only reveals itself on hover — which never
            // happens on a phone, leaving a borderless glyph with no affordance. Give it a resting fill.
            _ => state switch {
                ControlState.Pressed => _theme.ControlPressed,
                ControlState.Hovered => _theme.ControlHover,
                _ => _compact ? _theme.ControlHover : null,
            },
        };
        if (fill.HasValue) paint.AddRect(Bounds, fill.Value, radius);

        var filled = Tone != ToolbarTone.Default;
        var fg = filled ? _theme.OnPrimary : _theme.OnSurface;
        if (!Enabled) fg = StateStyle.Disabled(fg);

        var x = Bounds.X + (Bounds.Width - ContentWidth()) * 0.5f;
        var midY = Bounds.Y + Bounds.Height * 0.5f;

        if (Icon != null)
        {
            Icons.Draw(
                paint,
                Icon,
                new Rect(
                    x,
                    Bounds.Y,
                    IconSize,
                    Bounds.Height
                ),
                fg,
                IconSize
            );
            x += IconSize + (_labelW > 0f ? Gap : 0f);
        }

        if (_labelW > 0f)
        {
            var fs = _theme.FontSizeCaption;
            paint.AddText(
                Label!,
                x,
                midY + fs * 0.35f,
                fg,
                fs,
                fontWeight: FontWeight.Medium
            );
            x += _labelW;
        }

        if (Dropdown)
        {
            x += 2f;
            Icons.Draw(
                paint,
                Icons.DropDown,
                new Rect(
                    x,
                    Bounds.Y,
                    ChevW,
                    Bounds.Height
                ),
                filled ? fg : _theme.TextMuted,
                ChevW + 3f
            );
        }

        if (Focused && Enabled) paint.AddFocusRing(Bounds, radius, _theme);
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
        if (!Enabled || _pressed) return;
        _pressed = true;
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (_pressed && Enabled && Bounds.Contains(point.X, point.Y))
            OnPressed?.Invoke();
        if (_pressed)
        {
            _pressed = false;
            MarkNeedsPaint();
        }
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (scancode is 44 or 40) // Space or Enter
        {
            _pressed = down;
            MarkNeedsPaint();
            if (!down && Enabled) OnPressed?.Invoke();
        }
    }
}