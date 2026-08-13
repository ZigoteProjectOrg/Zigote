namespace Zigote.UI.Widgets;

/// <summary>
///     Marker interface for widgets that accept IME / printable text input via
///     <see cref="Widget.OnTextInput" /> but are not a <c>TextField</c>. The app's focus gate starts
///     the engine's text-input mode for any focused widget that is a <see cref="ITextInputClient" />
///     (in addition to <c>TextField</c>), so typed characters are delivered to it.
/// </summary>
public interface ITextInputClient
{
    /// <summary>
    ///     Whether this client needs a repaint every frame while focused, to animate a blinking caret.
    ///     A read-only / caret-less editor returns <c>false</c> so the frame loop can idle instead of
    ///     re-painting 60×/s for nothing. Defaults to <c>true</c> (matches a normal text caret).
    /// </summary>
    bool WantsCaretBlink => true;
}
