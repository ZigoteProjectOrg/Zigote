namespace Zigote.UI;

/// <summary>
///     Optional, app-supplied UI sound hooks. The framework is silent by default — an app (typically a
///     game) assigns these once to play a click/hover blip on <i>every</i> composed control without
///     wrapping each button. Invoked from <see cref="Widgets.Controls.Pressable" /> (the shared
///     interaction primitive behind Button/Chip/Checkbox/Radio/…), so any control built on it gets sound
///     for free. Unset = no sound, so the editor and gallery stay quiet.
/// </summary>
public static class UiFeedback
{
    /// <summary>Invoked when any Pressable-based control is activated (tap or Space/Enter).</summary>
    public static Action? Click { get; set; }

    /// <summary>Invoked when the pointer first enters a Pressable-based control.</summary>
    public static Action? Hover { get; set; }

    /// <summary>Invoked for each character committed to a focused text input (TextField / editor).</summary>
    public static Action? Type { get; set; }
}