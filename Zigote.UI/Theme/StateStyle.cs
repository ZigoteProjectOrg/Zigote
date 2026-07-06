using Zigote.Core;

namespace Zigote.UI.Theme;

/// <summary>
///     The interaction state of a control. Drives every stateful visual (fill, border, content
///     opacity) through one switch in <see cref="StateStyle" /> rather than ad-hoc per-widget logic.
/// </summary>
public enum ControlState
{
    Normal,
    Hovered,
    Pressed,
    Focused,
    Selected,
    Disabled,
    Invalid,
    Dragging,
    DropTarget,
}

/// <summary>
///     Centralised interaction-state modulation. Every control derives its hover / pressed / disabled
///     appearance from these constants and helpers instead of inventing its own <c>.Lighten(0.06f)</c>
///     /<c>.Darken(0.12f)</c>/<c>.WithAlpha(0.4f)</c> values, so feedback is uniform across the UI.
/// </summary>
public static class StateStyle
{
    /// <summary>Multiplier applied to a control's content opacity while hovered.</summary>
    public const float HoverOpacity = 0.9f;

    /// <summary>Multiplier applied while pressed.</summary>
    public const float PressedOpacity = 0.78f;

    /// <summary>Multiplier applied to disabled content.</summary>
    public const float DisabledOpacity = 0.4f;

    // Solid-fill tints (e.g. accent buttons): lighten on hover, darken on press.
    public const float HoverLighten = 0.06f;
    public const float PressedDarken = 0.10f;

    /// <summary>Tint a solid fill for the given interaction state.</summary>
    public static Color Fill(Color baseColor, bool hovered, bool pressed)
    {
        if (pressed) return baseColor.Darken(PressedDarken);
        if (hovered) return baseColor.Lighten(HoverLighten);
        return baseColor;
    }

    /// <summary>Apply the disabled opacity to <paramref name="c" />.</summary>
    public static Color Disabled(Color c)
    {
        return c.WithAlpha(c.A * DisabledOpacity);
    }

    /// <summary>Collapse the common hover/press/disable booleans into a <see cref="ControlState" />.</summary>
    public static ControlState StateOf(bool hovered, bool pressed, bool disabled = false,
        bool selected = false, bool focused = false)
    {
        if (disabled) return ControlState.Disabled;
        if (pressed) return ControlState.Pressed;
        if (selected) return ControlState.Selected;
        if (hovered) return ControlState.Hovered;
        if (focused) return ControlState.Focused;
        return ControlState.Normal;
    }

    /// <summary>
    ///     Resolve the opaque background of a standard control for an interaction state, straight from
    ///     the theme's <see cref="ThemeData.Control" />/<see cref="ThemeData.ControlHover" />/… tokens.
    /// </summary>
    public static Color ControlFill(ThemeData theme, ControlState state)
    {
        return state switch {
            ControlState.Hovered or ControlState.DropTarget => theme.ControlHover,
            ControlState.Pressed or ControlState.Dragging => theme.ControlPressed,
            ControlState.Disabled => theme.ControlDisabled,
            ControlState.Selected => theme.SelectionTint,
            _ => theme.Control,
        };
    }

    /// <summary>
    ///     Tint an arbitrary base fill (e.g. an accent button) for an interaction state — lighten on
    ///     hover, darken on press, fade when disabled.
    /// </summary>
    public static Color Tint(Color baseColor, ControlState state)
    {
        return state switch {
            ControlState.Pressed or ControlState.Dragging => baseColor.Darken(PressedDarken),
            ControlState.Hovered or ControlState.DropTarget => baseColor.Lighten(HoverLighten),
            ControlState.Disabled => Disabled(baseColor),
            _ => baseColor,
        };
    }
}