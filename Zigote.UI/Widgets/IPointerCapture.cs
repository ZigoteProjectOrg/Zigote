namespace Zigote.UI.Widgets;

/// <summary>
///     Marks a widget that claims the whole pointer gesture over its bounds — a
///     <c>Pressable</c> or a <c>Draggable</c>. When one is nested inside another (a button or a drag
///     handle on an activatable row), the inner one wins: see <c>Pressable.HitTest</c>.
/// </summary>
public interface IPointerCapture;
