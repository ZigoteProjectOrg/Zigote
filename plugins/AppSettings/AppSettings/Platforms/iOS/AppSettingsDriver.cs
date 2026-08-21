using Foundation;
using UIKit;

namespace AppSettings;

/// <summary>
///     iOS implementation — iOS exposes exactly one deep link into Settings, the app's own page
///     (plus a notifications page from iOS 16). Anything else, including the system location
///     page, is not linkable, so it lands on the app page rather than nowhere.
/// </summary>
internal static class AppSettingsDriver
{
    public static async Task<bool> OpenAsync(SettingsPage page)
    {
        string target = page == SettingsPage.Notifications && OperatingSystem.IsIOSVersionAtLeast(16)
            ? UIApplication.OpenNotificationSettingsUrlString
            : UIApplication.OpenSettingsUrlString;

        try
        {
            if (NSUrl.FromString(target) is not { } url) return false;
            return await UIApplication.SharedApplication.OpenUrlAsync(
                url, new UIApplicationOpenUrlOptions());
        }
        catch (Exception)
        {
            return false;
        }
    }
}
