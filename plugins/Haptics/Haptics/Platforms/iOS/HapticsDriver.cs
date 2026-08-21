using UIKit;

namespace Haptics;

/// <summary>
///     iOS implementation — the three UIFeedbackGenerator families, which is exactly the
///     vocabulary <see cref="Haptic" /> was shaped around. Generators must be used on the main
///     thread; <c>Prepare</c> is what keeps the first tap from arriving late.
/// </summary>
internal static class HapticsDriver
{
    // Taptic Engine on every supported iPhone; iPads have none and silently ignore the call.
    public static bool Supported => UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;

    public static bool Play(Haptic feedback)
    {
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            switch (feedback)
            {
                case Haptic.Selection:
                    using (var generator = new UISelectionFeedbackGenerator())
                    {
                        generator.Prepare();
                        generator.SelectionChanged();
                    }

                    break;
                case Haptic.Light or Haptic.Medium or Haptic.Heavy:
                    var style = feedback switch
                    {
                        Haptic.Light => UIImpactFeedbackStyle.Light,
                        Haptic.Heavy => UIImpactFeedbackStyle.Heavy,
                        _ => UIImpactFeedbackStyle.Medium
                    };
                    using (var generator = new UIImpactFeedbackGenerator(style))
                    {
                        generator.Prepare();
                        generator.ImpactOccurred();
                    }

                    break;
                default:
                    var type = feedback switch
                    {
                        Haptic.Success => UINotificationFeedbackType.Success,
                        Haptic.Warning => UINotificationFeedbackType.Warning,
                        _ => UINotificationFeedbackType.Error
                    };
                    using (var generator = new UINotificationFeedbackGenerator())
                    {
                        generator.Prepare();
                        generator.NotificationOccurred(type);
                    }

                    break;
            }
        });
        return Supported;
    }

    /// <summary>iOS has no arbitrary-duration vibration; the nearest honest thing is one heavy tap.</summary>
    public static bool Vibrate(int milliseconds, double amplitude) => Play(Haptic.Heavy);
}
