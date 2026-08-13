using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Focus;

/// <summary>
///     Marker for an overlay that must NOT auto-focus its first focusable when pushed. By default
///     <see cref="App" /> focuses the first focusable inside a newly-pushed overlay (modal
///     dialogs/forms) and restores the previous focus when it is popped. A non-modal tool overlay
///     (e.g. the devtools panel) implements this so opening it never steals focus from the app; its
///     controls are focused only when the user clicks them.
/// </summary>
public interface INoAutoFocus;
