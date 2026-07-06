namespace Zigote.UI.Theme;

/// <summary>
///     Animation timing tokens (seconds) — the motion counterpart of <see cref="Spacing" /> /
///     <see cref="Radii" />. Every widget transition feeds one of these into an
///     <c>AnimationController</c>, so the whole set shares one calm, consistent feel and can be retuned
///     in a single place. The controller is delta-time driven, so these are wall-clock durations that
///     look the same at any frame rate.
///     <para>Mutable statics: set them once at app startup to globally speed up or slow down motion.</para>
/// </summary>
public static class Motion
{
    /// <summary>Small, high-frequency flips — radio dot, chip colour crossfade, menu pop.</summary>
    public static float Fast = 0.2f;

    /// <summary>The default micro-interaction — checkbox tick, tab/segment slide, tree reveal, overlays.</summary>
    public static float Standard = 0.3f;

    /// <summary>Larger, more deliberate motion — full-surface entrances.</summary>
    public static float Slow = 0.45f;
}