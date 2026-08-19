using Android.App;
using Android.Content;

namespace UrlLauncher;

/// <summary>Android implementation — ACTION_VIEW from the application context.</summary>
internal static class UrlLauncherDriver
{
    public static Task<bool> OpenAsync(string url)
    {
        try
        {
            var uri = Android.Net.Uri.Parse(url);
            var intent = new Intent(Intent.ActionView, uri)
                // Started from outside an activity context, so it needs its own task.
                .AddFlags(ActivityFlags.NewTask);
            Application.Context.StartActivity(intent);
            return Task.FromResult(true);
        }
        catch (ActivityNotFoundException)
        {
            return Task.FromResult(false);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }
}
