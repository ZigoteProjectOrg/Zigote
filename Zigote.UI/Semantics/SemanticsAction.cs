namespace Zigote.UI.Semantics;

/// <summary>
///     The set of actions an assistive technology may invoke on a semantics node — the
///     platform-neutral
///     equivalent of NSAccessibility actions / UIA patterns. A bridge surfaces these so a screen
///     reader's
///     "activate" / "increment" / "dismiss" gestures route back into the widget that declared them.
/// </summary>
[Flags]
public enum SemanticsAction
{
    None = 0,

    /// <summary>Activate (click / press / toggle).</summary>
    Tap = 1 << 0,

    /// <summary>Move keyboard focus to this node.</summary>
    Focus = 1 << 1,

    /// <summary>Increase a ranged value (slider/stepper).</summary>
    Increase = 1 << 2,

    /// <summary>Decrease a ranged value.</summary>
    Decrease = 1 << 3,

    /// <summary>Replace the editable value (text field set-value).</summary>
    SetValue = 1 << 4,

    /// <summary>Scroll the node's content.</summary>
    Scroll = 1 << 5,

    /// <summary>Dismiss a transient/modal surface (Esc).</summary>
    Dismiss = 1 << 6,
}