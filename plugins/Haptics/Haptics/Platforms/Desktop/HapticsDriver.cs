namespace Haptics;

/// <summary>
///     Desktop implementation — there is no haptic engine on a laptop, so every call is a no-op
///     that says so. Callers do not branch on platform; they branch on false, or ignore it.
/// </summary>
internal static class HapticsDriver
{
    public static bool Supported => false;

    public static bool Play(Haptic feedback) => false;

    public static bool Vibrate(int milliseconds, double amplitude) => false;
}
