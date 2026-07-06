namespace Zigote.UI.Widgets.Focus;

/// <summary>
///     Implemented by overlays (dialogs, popovers, menus) that should close on Escape. When the user
///     presses Esc and no focused text editor consumes it, the app walks the overlay stack top-down and
///     calls <see cref="RequestDismiss" /> on the first dismissable overlay, stopping when one handles it.
/// </summary>
public interface IDismissableOverlay
{
    /// <summary>Attempt to dismiss. Return true if the press was consumed (the overlay closed/handled it).</summary>
    bool RequestDismiss();
}