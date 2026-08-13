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
            value1: Icon,
            value2: Label,
            value3: _hovered,
            value4: _pressed,
            value5: Enabled,
            value6: Tone
        );
    }

    private float ContentWidth()
    {
        float w = 0f;
        bool has = false;
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
        float fs = _theme.FontSizeCaption;
        _labelW = string.IsNullOrEmpty(Label)
            ? 0f
            : TextMeasure.Width(text: Label, fontSize: fs, weight: FontWeight.Medium);
        // The bar itself is already 44 tall; its buttons were a fixed 28, so on a phone nothing in
        // a touch-sized toolbar was actually touch-sized.
        float h = TouchMetrics.Pick(ControlMetrics.RegularHeight);
        _size = c.Constrain(
            new Size(width: MathF.Max(x: ContentWidth() + (PadX * 2f), y: h), height: h)
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
        float radius = Radii.Sm;
        var state = StateStyle.StateOf(hovered: _hovered, pressed: _pressed, disabled: !Enabled);

        // Fill: accent tones are always filled; the quiet default only fills on hover/press.
        Color? fill = Tone switch {
            ToolbarTone.Primary => StateStyle.Tint(baseColor: _theme.Accent, state: state),
            ToolbarTone.Danger => StateStyle.Tint(baseColor: _theme.Danger, state: state),
            ToolbarTone.Success => StateStyle.Tint(baseColor: _theme.Success, state: state),
            // The quiet tone is transparent at rest and only reveals itself on hover — which never
            // happens on a phone, leaving a borderless glyph with no affordance. Give it a resting fill.
            _ => state switch {
                ControlState.Pressed => _theme.ControlPressed,
                ControlState.Hovered => _theme.ControlHover,
                _ => _compact ? _theme.ControlHover : null,
            },
        };
        if (fill.HasValue) paint.AddRect(bounds: Bounds, color: fill.Value, radius: radius);

        bool filled = Tone != ToolbarTone.Default;
        var fg = filled ? _theme.OnPrimary : _theme.OnSurface;
        if (!Enabled) fg = StateStyle.Disabled(fg);

        float x = Bounds.X + ((Bounds.Width - ContentWidth()) * 0.5f);
        float midY = Bounds.Y + (Bounds.Height * 0.5f);

        if (Icon != null)
        {
            Icons.Draw(
                paint: paint,
                glyph: Icon,
                box: new Rect(
                    x: x,
                    y: Bounds.Y,
                    width: IconSize,
                    height: Bounds.Height
                ),
                color: fg,
                size: IconSize
            );
            x += IconSize + (_labelW > 0f ? Gap : 0f);
        }

        if (_labelW > 0f)
        {
            float fs = _theme.FontSizeCaption;
            paint.AddText(
                text: Label!,
                baselineX: x,
                baselineY: midY + (fs * 0.35f),
                color: fg,
                fontSize: fs,
                fontWeight: FontWeight.Medium
            );
            x += _labelW;
        }

        if (Dropdown)
        {
            x += 2f;
            Icons.Draw(
                paint: paint,
                glyph: Icons.DropDown,
                box: new Rect(
                    x: x,
                    y: Bounds.Y,
                    width: ChevW,
                    height: Bounds.Height
                ),
                color: filled ? fg : _theme.TextMuted,
                size: ChevW + 3f
            );
        }

        if (Focused && Enabled) paint.AddFocusRing(bounds: Bounds, radius: radius, theme: _theme);
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
        if (_pressed && Enabled && Bounds.Contains(px: point.X, py: point.Y))
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
