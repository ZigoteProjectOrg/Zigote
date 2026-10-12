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

    /// <summary>
    ///     For a caret that follows the standard blink (<see cref="CaretBlink" />): the
    ///     <c>App.Time</c> its cycle last restarted. The frame loop then repaints the client only
    ///     when the caret actually appears or disappears — twice a cycle — and sleeps in between,
    ///     instead of repainting every frame. <c>null</c> (the default) means the caret animates
    ///     some other way and keeps the per-frame repaint.
    /// </summary>
    float? CaretBlinkBase => null;
}

/// <summary>The standard caret blink: shown for 0.6 s, hidden for 0.46 s, from a restart time.</summary>
public static class CaretBlink
{
    public const float Period = 1.06f;
    public const float ShownFor = 0.6f;

    /// <summary>Whether the caret is drawn at <paramref name="time" />.</summary>
    public static bool Shown(float time, float blinkBase) => (time - blinkBase) % Period < ShownFor;

    /// <summary>Seconds from <paramref name="time" /> until the caret next appears or disappears.</summary>
    public static float ChangeIn(float time, float blinkBase)
    {
        float phase = (time - blinkBase) % Period;
        if (phase < 0f) phase += Period;
        return phase < ShownFor ? ShownFor - phase : Period - phase;
    }
}
