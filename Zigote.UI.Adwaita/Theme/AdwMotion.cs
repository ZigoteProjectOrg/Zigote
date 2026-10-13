using Zigote.Core.Animation;

namespace Zigote.UI.Adwaita;

/// <summary>
///     The library's motion vocabulary — the springs Adwaita widgets animate with, in one place, so
///     the whole set can be retuned at once the way <see cref="AdwMetrics" /> retunes its shape.
///     <para>
///         These are springs rather than durations because Adwaita motion is nearly all
///         interruptible: a switch is toggled twice in a row, a sidebar is folded while it is still
///         unfolding, a row is expanded and collapsed on the same click. A duration restarts from a
///         standstill each time; a spring bends toward the new target keeping the speed it already
///         had. <see cref="Durations" /> stays the vocabulary for the timings that are not springs
///         (a colour crossfade has no velocity to preserve).
///     </para>
///     <para>
///         Mutable statics, like the rest of the design tokens: assign at startup to make the whole
///         library calmer or livelier.
///     </para>
/// </summary>
public static class AdwMotion
{
    /// <summary>
    ///     Small controls that flip — a switch knob, a chevron, a check. Quick and just barely
    ///     damped, so a double-tap reads as one continuous movement.
    /// </summary>
    public static MotionSpec Control = MotionSpec.Spring(response: 0.24f, damping: 0.9f);

    /// <summary>
    ///     Something appearing over the content — a popover, a dialog, a toast. Slightly slower than
    ///     <see cref="Control" /> because it covers more ground.
    /// </summary>
    public static MotionSpec Overlay = MotionSpec.Spring(response: 0.3f, damping: 0.95f);

    /// <summary>
    ///     A surface changing size or sliding in — a banner unrolling, an expander opening, a
    ///     sidebar folding. Critically damped: no overshoot, because the thing that would overshoot
    ///     is a layout edge.
    /// </summary>
    public static MotionSpec Reveal = MotionSpec.Spring(response: 0.36f, damping: 1f);

    /// <summary>
    ///     A value settling to a new one — a progress bar advancing. Deliberately unhurried: it
    ///     reads as the number moving, not as the widget animating.
    /// </summary>
    public static MotionSpec Value = MotionSpec.Spring(response: 0.45f, damping: 1f);

    /// <summary>
    ///     Seconds a control's fill takes to follow the pointer — hover, press, selection. A colour
    ///     has no velocity to keep, so this one is a duration.
    /// </summary>
    public static float Hover = 0.1f;

    /// <summary>Seconds one piece of content takes to dissolve into another — a view stack's pages.</summary>
    public static float Crossfade = 0.2f;
}
