using Android.App;

namespace Notifications;

/// <summary>
///     Android implementation — one <see cref="NotificationChannel" /> named after the app
///     (created lazily), one notification per slot. From API 33 the OS silently drops posts when
///     <c>POST_NOTIFICATIONS</c> is not held; asking for it is the Permissions plugin's job.
/// </summary>
internal sealed class NotificationsDriver : IDisposable
{
    private readonly string _appId;
    private readonly string _appName;
    private bool _channelReady;

    public NotificationsDriver(string appId, string appName, Action<string> onAction)
    {
        _appId = appId;
        _appName = appName;
        // ponytail: no action buttons on Android yet — they need a BroadcastReceiver plus a
        // PendingIntent per button. Add when an app actually wires ActionInvoked on Android.
        _ = onAction;
    }

    public bool SupportsActions => false;

    public Task StartAsync() => Task.CompletedTask;

    public void Show(int slot, Notification notification)
    {
        var context = Application.Context;
        var manager = NotificationManager.FromContext(context);
        if (manager is null) return;

        if (!_channelReady)
        {
            manager.CreateNotificationChannel(
                new NotificationChannel(_appId, _appName, NotificationImportance.Default));
            _channelReady = true;
        }

        int icon = context.ApplicationInfo?.Icon ?? 0;
        if (icon == 0) icon = global::Android.Resource.Drawable.SymDefAppIcon;

        using var builder = new global::Android.App.Notification.Builder(context, _appId);
        builder
            .SetContentTitle(notification.Title)
            .SetContentText(notification.Body)
            .SetSmallIcon(icon)
            .SetOngoing(notification.Resident);
        manager.Notify(slot, builder.Build());
    }

    public void Close(int slot) => NotificationManager.FromContext(Application.Context)?.Cancel(slot);

    public void Shutdown()
    {
    }

    public void Dispose()
    {
    }
}
