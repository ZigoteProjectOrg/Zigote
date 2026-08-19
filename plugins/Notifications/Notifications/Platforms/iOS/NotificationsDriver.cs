namespace Notifications;

/// <summary>iOS: not implemented yet — UNUserNotificationCenter is future work. Every call is a
///     safe no-op, matching the shared contract.</summary>
internal sealed class NotificationsDriver : IDisposable
{
    public NotificationsDriver(string appId, string appName, Action<string> onAction)
    {
        _ = appId;
        _ = appName;
        _ = onAction;
    }

    public bool SupportsActions => false;

    public Task StartAsync() => Task.CompletedTask;

    public void Show(int slot, Notification notification)
    {
    }

    public void Close(int slot)
    {
    }

    public void Shutdown()
    {
    }

    public void Dispose()
    {
    }
}
