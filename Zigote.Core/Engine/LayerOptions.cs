namespace Zigote.Core.Engine;

/// <summary>Which stack a shell surface lives in, bottom to top (wlr-layer-shell's `layer`).</summary>
public enum ShellLayer : uint
{
    Background = 0,
    Bottom = 1,
    Top = 2,
    Overlay = 3,
}

/// <summary>Edges a shell surface sticks to. Opposite edges together stretch it across the output.</summary>
[Flags]
public enum LayerAnchor : uint
{
    None = 0,
    Top = 1,
    Bottom = 2,
    Left = 4,
    Right = 8,
}

/// <summary>How a shell surface takes keyboard focus.</summary>
public enum LayerKeyboard : uint
{
    None = 0,

    /// <summary>Grabs the keyboard while mapped — a launcher or lock screen.</summary>
    Exclusive = 1,

    /// <summary>Focused like a normal window, on click.</summary>
    OnDemand = 2,
}

/// <summary>
///     Placement of a desktop-shell window (panel, dock, launcher…) — see
///     <see cref="ZigoteEngine.CreateLayerWindow" />. <paramref name="ExclusiveZone" /> reserves
///     that many logical pixels along the anchored edge so toplevels are kept off the surface;
///     -1 asks to be placed ignoring other surfaces' zones (a full-screen overlay).
/// </summary>
public readonly record struct LayerOptions(
    ShellLayer Layer,
    LayerAnchor Anchor,
    int ExclusiveZone = 0,
    LayerKeyboard Keyboard = LayerKeyboard.None
);
