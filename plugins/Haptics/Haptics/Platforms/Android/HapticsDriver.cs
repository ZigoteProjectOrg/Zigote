using Android.App;
using Android.Content;
using Android.OS;

namespace Haptics;

/// <summary>
///     Android implementation — one <see cref="Vibrator" />, reached through VibratorManager on
///     API 31+ and the system service before it, driven with <see cref="VibrationEffect" />
///     waveforms. Hardware without amplitude control gets the same timings at full strength.
/// </summary>
internal static class HapticsDriver
{
    private static Vibrator? _vibrator;

    private static Vibrator? Device
    {
        get
        {
            if (_vibrator is not null) return _vibrator;
            var context = Application.Context;
            _vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? ((VibratorManager?)context.GetSystemService(Context.VibratorManagerService))
                ?.DefaultVibrator
                : (Vibrator?)context.GetSystemService(Context.VibratorService);
            return _vibrator;
        }
    }

    public static bool Supported => Device?.HasVibrator ?? false;

    public static bool Play(Haptic feedback)
    {
        var (timings, amplitude) = HapticsPlugin.PatternFor(feedback);
        return Vibrate(timings, amplitude);
    }

    public static bool Vibrate(int milliseconds, double amplitude)
        => Vibrate([0, milliseconds], amplitude);

    private static bool Vibrate(long[] timings, double amplitude)
    {
        var vibrator = Device;
        if (vibrator is null || !vibrator.HasVibrator) return false;

        try
        {
            // Odd entries are the "on" slots; even ones are the gaps between them.
            int[] amplitudes = new int[timings.Length];
            int strength = (int)Math.Clamp(Math.Round(amplitude * 255), 1, 255);
            for (int i = 1; i < timings.Length; i += 2) amplitudes[i] = strength;

            var effect = vibrator.HasAmplitudeControl
                ? VibrationEffect.CreateWaveform(timings, amplitudes, -1)
                : VibrationEffect.CreateWaveform(timings, -1);
            vibrator.Vibrate(effect);
            return true;
        }
        catch (Exception)
        {
            // No VIBRATE permission, or a vendor vibrator that rejects the effect.
            return false;
        }
    }
}
