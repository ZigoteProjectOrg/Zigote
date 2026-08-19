namespace Notifications;

/// <summary>
///     One notification's content. Everything beyond title and body is optional and degrades to
///     nothing where the platform cannot show it.
/// </summary>
/// <param name="Title">The summary line.</param>
/// <param name="Body">The text under it.</param>
public sealed record Notification(string Title, string Body)
{
    /// <summary>A file path or theme icon name; the client's app id when null.</summary>
    public string? IconPath { get; init; }

    /// <summary>
    ///     Buttons, as (key, label) pairs — the key comes back through
    ///     <see cref="NotificationClient.ActionInvoked" />. Only attached where
    ///     <see cref="NotificationClient.SupportsActions" /> says the daemon can show them.
    /// </summary>
    public IReadOnlyList<(string Key, string Label)> Actions { get; init; } = [];

    /// <summary>
    ///     A control surface rather than a popup: pressing a button does not dismiss it, and it
    ///     never expires on its own — it stays until <see cref="NotificationClient.Close" />.
    /// </summary>
    public bool Resident { get; init; }

    /// <summary>Skip the notification list; show and vanish.</summary>
    public bool Transient { get; init; }

    /// <summary>A freedesktop category hint (e.g. "x-gnome.music"); omitted when null.</summary>
    public string? Category { get; init; }

    /// <summary>How hard the notification may interrupt. Low never pops over the user's work.</summary>
    public NotificationUrgency Urgency { get; init; } = NotificationUrgency.Normal;
}

/// <summary>The freedesktop urgency levels, by their wire values.</summary>
public enum NotificationUrgency : byte
{
    Low = 0,
    Normal = 1,
    Critical = 2,
}

/// <summary>
///     Notifications — post, replace and withdraw notifications, with action buttons where the
///     platform supports them (Linux daemons that list the <c>actions</c> capability).
///     <para>
///         Instance-based because the Linux backend owns a D-Bus connection: construct one per
///         app, <see cref="StartAsync" /> it once (never throws — a machine with no notification
///         daemon is a normal machine), and every call before or without a successful start is a
///         safe no-op.
///     </para>
///     <para>
///         A slot is one on-screen notification: <see cref="Show" /> with the same slot rewrites
///         it in place instead of stacking a new popup, which is what keeps a now-playing style
///         notification permanent rather than flickering.
///     </para>
/// </summary>
public sealed class NotificationClient : IDisposable
{
    private readonly NotificationsDriver _driver;

    /// <param name="appId">The desktop entry / notification channel id, e.g. "dev.zigote.Timbre".</param>
    /// <param name="appName">The human-readable application name.</param>
    public NotificationClient(string appId, string appName)
    {
        _driver = new NotificationsDriver(appId, appName, key => ActionInvoked?.Invoke(key));
    }

    /// <summary>
    ///     A button was pressed, by its key. Arrives on a non-UI thread (the D-Bus read loop on
    ///     Linux) — post to your UI thread before touching widgets or state.
    /// </summary>
    public event Action<string>? ActionInvoked;

    /// <summary>Whether <see cref="Notification.Actions" /> will actually be shown here.</summary>
    public bool SupportsActions => _driver.SupportsActions;

    /// <summary>Connect to the platform's notification service. Never throws.</summary>
    public Task StartAsync() => _driver.StartAsync();

    /// <summary>Post the notification, or rewrite the one already in <paramref name="slot" />.</summary>
    public void Show(Notification notification, int slot = 0) => _driver.Show(slot, notification);

    /// <summary>Withdraw the notification in <paramref name="slot" />, if any.</summary>
    public void Close(int slot = 0) => _driver.Close(slot);

    /// <summary>
    ///     Withdraw everything and wait (briefly, bounded) for the calls to leave the socket —
    ///     for process exit, where fire-and-forget races the shutdown and a notification for an
    ///     app that is gone never goes away on its own.
    /// </summary>
    public void Shutdown() => _driver.Shutdown();

    public void Dispose() => _driver.Dispose();
}
