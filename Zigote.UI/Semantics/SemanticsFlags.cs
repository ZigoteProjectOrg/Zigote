namespace Zigote.UI.Semantics;

/// <summary>
///     Boolean accessibility state for a semantics node. A bridge maps the set onto platform state
///     attributes (e.g. <c>AXValue</c>/<c>aria-checked</c>/<c>aria-disabled</c>). State that is
///     naturally
///     <c>true</c> for most nodes (enabled) is modelled as the absence of the negative flag so a
///     freshly-built configuration reads as "enabled, visible".
/// </summary>
[Flags]
public enum SemanticsFlags
{
    None = 0,

    /// <summary>The node can take keyboard focus.</summary>
    Focusable = 1 << 0,

    /// <summary>The node currently holds keyboard focus.</summary>
    Focused = 1 << 1,

    /// <summary>The node is disabled — present but not interactive.</summary>
    Disabled = 1 << 2,

    /// <summary>The node exposes a checked/unchecked state (checkbox/switch/radio).</summary>
    Checkable = 1 << 3,

    /// <summary>The node is currently checked/on.</summary>
    Checked = 1 << 4,

    /// <summary>Tristate: neither checked nor unchecked (a mixed checkbox).</summary>
    Mixed = 1 << 5,

    /// <summary>The node exposes a selected/unselected state (tab/list item).</summary>
    Selectable = 1 << 6,

    /// <summary>The node is currently selected.</summary>
    Selected = 1 << 7,

    /// <summary>An editable field whose contents cannot be modified.</summary>
    ReadOnly = 1 << 8,

    /// <summary>An editable field whose contents are masked (password).</summary>
    Obscured = 1 << 9,

    /// <summary>An editable field that accepts multiple lines.</summary>
    Multiline = 1 << 10,

    /// <summary>A modal node that traps focus while present (dialog/sheet).</summary>
    Modal = 1 << 11,

    /// <summary>Content changes here should be announced even without focus (alert/snackbar).</summary>
    LiveRegion = 1 << 12,

    /// <summary>Present in the tree but hidden from assistive tech (decorative / off-screen).</summary>
    Hidden = 1 << 13,
}