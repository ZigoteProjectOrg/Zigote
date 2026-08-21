using Android.App;
using Android.Content;
using Android.Provider;

namespace AppSettings;

/// <summary>
///     Android implementation — one <c>Settings.ACTION_*</c> intent per page, started in its own
///     task because there is no activity context here.
/// </summary>
internal static class AppSettingsDriver
{
    public static Task<bool> OpenAsync(SettingsPage page)
    {
        var context = Application.Context;
        string package = context.PackageName!;

        var intent = page switch
        {
            SettingsPage.Notifications => new Intent(Settings.ActionAppNotificationSettings)
                .PutExtra(Settings.ExtraAppPackage, package),
            SettingsPage.Location => new Intent(Settings.ActionLocationSourceSettings),
            _ => new Intent(
                Settings.ActionApplicationDetailsSettings,
                Android.Net.Uri.Parse("package:" + package))
        };

        try
        {
            context.StartActivity(intent.AddFlags(ActivityFlags.NewTask));
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            // A ROM without that settings screen — rare, but it is an ActivityNotFoundException,
            // not a crash the app deserves.
            return Task.FromResult(false);
        }
    }
}
