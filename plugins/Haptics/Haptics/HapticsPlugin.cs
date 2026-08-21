namespace Haptics;

/// <summary>The feedback vocabulary every mobile OS agrees on.</summary>
public enum Haptic
{
    /// <summary>A value changed under the finger — a picker row, a segmented control.</summary>
    Selection,

    /// <summary>A light tap — a small control committed.</summary>
    Light,

    /// <summary>A medium tap — the default "that did something".</summary>
    Medium,

    /// <summary>A heavy tap — a big, deliberate action.</summary>
    Heavy,

    /// <summary>Two rising pulses — an operation finished.</summary>
    Success,

    /// <summary>Two pulses — something needs attention before continuing.</summary>
    Warning,

    /// <summary>Three sharp pulses — the operation failed.</summary>
    Error
}

/// <summary>
///     Haptics — the <c>vibration</c> slot from the plugin roadmap. Static, nothing to register
///     with <c>PluginHost</c>. Every call answers false where there is no haptic engine (all
///     desktops, a phone with vibration disabled), which is a normal answer, not an error.
/// </summary>
public static class HapticsPlugin
{
    /// <summary>True where haptics can actually be felt — false on desktop and on hardware without a vibrator.</summary>
    public static bool Supported => HapticsDriver.Supported;

    /// <summary>Play one of the standard feedbacks. False if nothing was played.</summary>
    public static bool Play(Haptic feedback) => HapticsDriver.Play(feedback);

    /// <summary>
    ///     Buzz for an arbitrary duration — a game effect rather than UI feedback. Clamped to
    ///     5 seconds; iOS has no arbitrary-duration API and plays <see cref="Haptic.Heavy" />
    ///     instead.
    /// </summary>
    /// <param name="amplitude">0–1; platforms without amplitude control treat anything above 0 as "on".</param>
    public static bool Vibrate(TimeSpan duration, double amplitude = 1.0)
    {
        int milliseconds = (int)Math.Clamp(duration.TotalMilliseconds, 0, 5000);
        if (milliseconds == 0 || amplitude <= 0) return false;
        return HapticsDriver.Vibrate(milliseconds, Math.Clamp(amplitude, 0, 1));
    }

    /// <summary>
    ///     The vibration pattern behind each feedback, as alternating off/on milliseconds — the
    ///     shape Android's waveform API wants, and the reason the patterns live in shared code
    ///     where they can be read and tested rather than inside the driver.
    /// </summary>
    internal static (long[] Timings, double Amplitude) PatternFor(Haptic feedback) => feedback switch
    {
        Haptic.Selection => ([0, 8], 0.4),
        Haptic.Light => ([0, 15], 0.5),
        Haptic.Medium => ([0, 30], 0.75),
        Haptic.Heavy => ([0, 50], 1.0),
        Haptic.Success => ([0, 25, 80, 45], 0.8),
        Haptic.Warning => ([0, 45, 90, 45], 0.8),
        Haptic.Error => ([0, 30, 60, 30, 60, 60], 1.0),
        _ => ([0, 30], 0.75)
    };
}
