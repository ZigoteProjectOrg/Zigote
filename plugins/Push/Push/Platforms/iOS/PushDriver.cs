using Foundation;
using UIKit;
using UserNotifications;

namespace Push;

/// <summary>
///     iOS implementation — ask for notification authorization, then register with APNs. The
///     token itself is delivered to the app delegate, which forwards it with one line:
///     <code>
///   public override void RegisteredForRemoteNotifications(UIApplication app, NSData token)
///       => PushPlugin.DeliverToken(Convert.ToHexString(token.ToArray()).ToLowerInvariant());
/// </code>
/// </summary>
internal static class PushDriver
{
    public static bool Available => true;

    public static async Task RegisterAsync()
    {
        var granted = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound);
        // A refused prompt still registers: data-only pushes are delivered without it, and the
        // user can turn alerts back on later without the app re-registering.
        _ = granted;

        UIApplication.SharedApplication.InvokeOnMainThread(
            () => UIApplication.SharedApplication.RegisterForRemoteNotifications());
    }
}
