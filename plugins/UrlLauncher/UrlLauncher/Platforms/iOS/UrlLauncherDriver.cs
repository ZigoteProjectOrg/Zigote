using Foundation;
using UIKit;

namespace UrlLauncher;

/// <summary>iOS implementation — UIApplication answers whether anything took the URL.</summary>
internal static class UrlLauncherDriver
{
    public static async Task<bool> OpenAsync(string url)
    {
        try
        {
            if (NSUrl.FromString(url) is not { } nsUrl) return false;
            return await UIApplication.SharedApplication.OpenUrlAsync(
                nsUrl, new UIApplicationOpenUrlOptions());
        }
        catch (Exception)
        {
            return false;
        }
    }
}
