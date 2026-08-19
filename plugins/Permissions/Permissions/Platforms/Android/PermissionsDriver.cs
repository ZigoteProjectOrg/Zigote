using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Permissions;

/// <summary>
///     Android: <c>requestPermissions</c> delivers its answer to an Activity, and the app's only
///     activity belongs to the engine — so the ask happens in a throwaway transparent activity
///     started for the purpose, which reports back and finishes.
/// </summary>
internal static class PermissionsDriver
{
    public static bool IsGranted(ZigotePermission permission) => Needed(permission).Length == 0;

    public static async Task<bool> RequestAsync(ZigotePermission permission)
    {
        var needed = Needed(permission);
        // Nothing to ask for: either already held, or the OS is old enough that the permission
        // is granted at install time. Answering "granted" without launching anything keeps the
        // common case (every launch after the first) free of a flashing activity.
        if (needed.Length == 0) return true;

        // One static handoff, safe because PermissionsPlugin serializes requests.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PermissionActivity.Pending = tcs;

        var context = Application.Context;
        var intent = new Intent(context, typeof(PermissionActivity))
            .PutExtra(PermissionActivity.PermissionsExtra, needed)
            // Started from outside an activity context, so it needs its own task.
            .AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    ///     Which manifest permissions back a capability, minus the ones already held.
    ///     <para>
    ///         The media sets are version-dependent and not merely renamed: READ_MEDIA_* exist
    ///         from API 33 and READ_EXTERNAL_STORAGE is capped at 32 in the manifest, so asking
    ///         for the wrong one is silently refused rather than reported.
    ///     </para>
    /// </summary>
    private static string[] Needed(ZigotePermission permission)
    {
        string[] wanted = permission switch
        {
            ZigotePermission.Notifications => OperatingSystem.IsAndroidVersionAtLeast(33)
                ? [global::Android.Manifest.Permission.PostNotifications]
                : [],
            ZigotePermission.Camera => [global::Android.Manifest.Permission.Camera],
            ZigotePermission.Microphone => [global::Android.Manifest.Permission.RecordAudio],
            ZigotePermission.MediaAudio => OperatingSystem.IsAndroidVersionAtLeast(33)
                ? [global::Android.Manifest.Permission.ReadMediaAudio]
                : [global::Android.Manifest.Permission.ReadExternalStorage],
            ZigotePermission.MediaImages => OperatingSystem.IsAndroidVersionAtLeast(33)
                ? [global::Android.Manifest.Permission.ReadMediaImages]
                : [global::Android.Manifest.Permission.ReadExternalStorage],
            ZigotePermission.MediaVideo => OperatingSystem.IsAndroidVersionAtLeast(33)
                ? [global::Android.Manifest.Permission.ReadMediaVideo]
                : [global::Android.Manifest.Permission.ReadExternalStorage],
            ZigotePermission.LocationWhenInUse =>
                [global::Android.Manifest.Permission.AccessFineLocation],
            _ => [],
        };

        var context = Application.Context;
        return wanted.Where(p => context.CheckSelfPermission(p) != Permission.Granted).ToArray();
    }
}

/// <summary>
///     The invisible activity that exists only to receive an answer. Started, asks its one
///     question, completes the pending task and finishes — the user sees the system dialog and
///     nothing else, because the theme is fully transparent and no content view is ever set.
/// </summary>
[Activity(
    Name = "dev.zigote.plugins.permissions.PermissionActivity",
    Exported = false,
    Theme = "@android:style/Theme.Translucent.NoTitleBar",
    // Excluded from recents: it is machinery, not a place the user can go back to.
    ExcludeFromRecents = true
)]
public sealed class PermissionActivity : Activity
{
    internal const string PermissionsExtra = "zigote.permissions";
    private const int RequestCode = 100;

    /// <summary>The one in-flight request's completion, set before the activity starts.</summary>
    internal static TaskCompletionSource<bool>? Pending;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var permissions = Intent?.GetStringArrayExtra(PermissionsExtra) ?? [];
        if (permissions.Length == 0)
        {
            Done(true);
            return;
        }

        RequestPermissions(permissions, RequestCode);
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RequestCode) return;
        // All-or-nothing: every permission asked for in one call belongs to one capability, so
        // a partial grant is not a usable half.
        Done(grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted));
    }

    private void Done(bool granted)
    {
        Pending?.TrySetResult(granted);
        Pending = null;
        Finish();
    }

    protected override void OnDestroy()
    {
        // Covers the paths where no result ever arrives (the activity is killed): a request
        // that hangs forever would wedge the serialized queue behind it. TrySetResult is a
        // no-op when the real answer already landed.
        Pending?.TrySetResult(false);
        Pending = null;
        base.OnDestroy();
    }
}
