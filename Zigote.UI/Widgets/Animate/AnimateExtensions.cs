namespace Zigote.UI.Widgets;

/// <summary>
///     The <c>zigote_animate</c> entry point: <c>widget.Animate()</c> wraps any widget in an
///     <see cref="Animate" /> so effects can be chained fluently, mirroring flutter_animate's
///     <c>.animate()</c>:
///     <code>
///     new Label("Hello").Animate().Fade(duration: 500.ms).Scale(delay: 500.ms);
///     </code>
/// </summary>
public static class AnimateExtensions
{
    /// <summary>Wrap this widget in an <see cref="Animate" /> for fluent effect chaining.</summary>
    public static Animate Animate(this Widget child)
    {
        return new Animate(child);
    }

    /// <summary>
    ///     Wrap this widget in an <see cref="Animate" /> configured for a state-driven transition —
    ///     the returned <see cref="Animate" /> plays forward/reverse as <paramref name="target" />
    ///     flips between 1 and 0.
    /// </summary>
    public static Animate Animate(this Widget child, float target)
    {
        return new Animate(child) { Target = target };
    }
}