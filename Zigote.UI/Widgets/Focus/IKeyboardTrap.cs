using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Focus;

/// <summary>
///     Implemented by a focusable widget that wants to keep the app-level focus-navigation keys —
///     Tab / Shift-Tab (focus traversal), Escape (overlay dismiss), and the arrow keys (directional
///     nav) — for itself while it holds focus, instead of letting <see cref="App" /> consume them
///     first. The devtools console field uses this so Tab drives command auto-complete and Esc blurs
///     the field rather than closing the panel. The widget still receives these keys through
///     <see cref="Widget.OnKey" />.
/// </summary>
public interface IKeyboardTrap;
