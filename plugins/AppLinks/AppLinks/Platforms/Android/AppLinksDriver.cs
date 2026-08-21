using Android.App;
using Android.Content;

namespace AppLinks;

/// <summary>
///     Android implementation — the OS owns single-instance behaviour (that is what
///     <c>launchMode</c> is for), so starting is only about the intent the app was launched
///     with. Later links arrive through the activity's <c>OnNewIntent</c>, which forwards them
///     with one call to <c>AppLinksPlugin.Deliver</c>; the app declares the intent filters that
///     make its links its own.
/// </summary>
internal static class AppLinksDriver
{
    public static Task<bool> StartAsync(string appId, string[] links, Action<string> deliver)
        => Task.FromResult(true);

    /// <summary>The data URI of the intent that launched the app, if it was launched by a link.</summary>
    public static Uri? LaunchLink()
    {
        try
        {
            var intent = Application.Context.PackageManager?
                .GetLaunchIntentForPackage(Application.Context.PackageName!);
            string? data = intent?.DataString;
            return data is not null && Uri.TryCreate(data, UriKind.Absolute, out var uri) ? uri : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
