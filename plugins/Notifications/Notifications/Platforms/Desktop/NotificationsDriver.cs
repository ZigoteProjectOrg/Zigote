using Tmds.DBus.Protocol;

namespace Notifications;

/// <summary>
///     Desktop implementation: <c>org.freedesktop.Notifications</c> over D-Bus on Linux, a no-op
///     everywhere else (Windows toasts and macOS UNUserNotificationCenter are future work).
///     <para>
///         Spoken over D-Bus rather than shelled out to <c>notify-send</c>, and the reason is the
///         one thing <c>notify-send</c> structurally cannot do: <b>actions</b>. A button on a
///         notification is a callback — the daemon sends <c>ActionInvoked</c> back to the process
///         that posted it — so a helper that exits the moment it has posted can never receive one.
///     </para>
/// </summary>
internal sealed class NotificationsDriver : IDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string Interface = "org.freedesktop.Notifications";

    private readonly string _appId;
    private readonly string _appName;
    private readonly Action<string> _onAction;

    private DBusConnection? _connection;
    private IDisposable? _subscription;
    private Task? _closing;

    /// <summary>slot → the daemon's id for it, so the next Show replaces it in place. Guarded by
    ///     itself: Show/Close run on the caller's thread, the id lands from a continuation.</summary>
    private readonly Dictionary<int, uint> _ids = [];

    public NotificationsDriver(string appId, string appName, Action<string> onAction)
    {
        _appId = appId;
        _appName = appName;
        _onAction = onAction;
    }

    public bool SupportsActions { get; private set; }

    /// <summary>
    ///     Connect, learn whether the daemon does actions, and subscribe to button presses. Never
    ///     throws: a machine with no notification daemon is a normal machine.
    /// </summary>
    public async Task StartAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            var address = DBusAddress.Session;
            if (address is null) return;

            // Our own connection: this both calls out and listens for a signal aimed at us, and
            // the shared autoconnect connection permits neither a bare send nor a match rule.
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();

            SupportsActions = await SupportsActionsAsync(connection);

            // The subscription is kept: it is a disposable observer, and dropping the handle on the
            // floor lets it be collected — a button that works until the first GC is worse than one
            // that never worked at all.
            // The handler shape matters. Tmds recommends the Action<Notification<T>> overload, and
            // with it no signal is ever delivered here — it dispatches on a captured
            // synchronization context, and a Zigote app has none, so the callback is simply never
            // invoked. The older overload takes the flag explicitly and works. A button that does
            // nothing is worse than an obsolete-API warning.
#pragma warning disable CS0618
            _subscription = await connection.AddMatchAsync(
                new MatchRule
                {
                    // No sender: the daemon signs its signals with its unique name while the rule
                    // would carry the well-known one. Path, interface and member are specific
                    // enough, and the id check below is what decides a press is ours.
                    Type = MessageType.Signal,
                    Path = ObjectPath,
                    Interface = Interface,
                    Member = "ActionInvoked"
                },
                // (u id, s action_key), read on the D-Bus thread.
                static (Message message, object? _) =>
                {
                    var reader = message.GetBodyReader();
                    var id = reader.ReadUInt32();
                    return (Id: id, Key: reader.ReadString());
                },
                static (Exception? error, (uint Id, string Key) action, object? _, object? state) =>
                {
                    if (error is not null) return;
                    ((NotificationsDriver)state!).OnActionInvoked(action.Id, action.Key);
                },
                ObserverFlags.None,
                null,
                this,
                false
            );
#pragma warning restore CS0618

            _connection = connection;
        }
        catch (Exception)
        {
            // No daemon, no bus, no permission: the app works, it just says nothing.
        }
    }

    /// <summary>
    ///     A button was pressed. Arrives on the D-Bus thread; forwarding to the UI thread is the
    ///     subscriber's job — see <see cref="NotificationClient.ActionInvoked" />.
    /// </summary>
    private void OnActionInvoked(uint id, string key)
    {
        try
        {
            bool ours;
            lock (_ids)
            {
                ours = _ids.ContainsValue(id);
            }

            if (ours) _onAction(key);
        }
        catch (Exception)
        {
            // A malformed signal is the daemon's problem, not a reason to fall over.
        }
    }

    public void Show(int slot, Notification notification)
    {
        if (_connection is not { } connection) return;
        try
        {
            uint replaces;
            lock (_ids)
            {
                _ids.TryGetValue(slot, out replaces);
            }

            MessageBuffer request;
            // Not a `using` variable: MessageWriter is a ref struct and the writes need it by
            // reference, which a using variable forbids.
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(
                    Service, ObjectPath, Interface, "Notify", "susssasa{sv}i");

                writer.WriteString(_appName); // app_name
                writer.WriteUInt32(replaces); // replaces_id: 0 posts a new one
                writer.WriteString(notification.IconPath ?? _appId); // path or theme icon name
                writer.WriteString(notification.Title);
                writer.WriteString(notification.Body);

                // actions: [key, label, key, label, …]. Keys are the caller's; labels are drawn.
                // Only attached when the daemon lists the capability — posting them to one that
                // does not puts a button nobody can press on the popup, or drops it entirely.
                var actions = writer.WriteArrayStart(DBusType.String);
                if (SupportsActions)
                {
                    foreach (var (key, label) in notification.Actions)
                    {
                        writer.WriteString(key);
                        writer.WriteString(label);
                    }
                }

                writer.WriteArrayEnd(actions);

                var hints = writer.WriteDictionaryStart();
                // Ties the popup to the app's .desktop entry, and so to its icon and name.
                writer.WriteDictionaryEntryStart();
                writer.WriteString("desktop-entry");
                writer.WriteVariant(VariantValue.String(_appId));
                writer.WriteDictionaryEntryStart();
                writer.WriteString("transient");
                writer.WriteVariant(VariantValue.Bool(notification.Transient));
                // Resident: pressing a button acts without dismissing the notification.
                writer.WriteDictionaryEntryStart();
                writer.WriteString("resident");
                writer.WriteVariant(VariantValue.Bool(notification.Resident));
                if (notification.Category is { } category)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("category");
                    writer.WriteVariant(VariantValue.String(category));
                }

                writer.WriteDictionaryEntryStart();
                writer.WriteString("urgency");
                writer.WriteVariant(VariantValue.Byte((byte)notification.Urgency));

                writer.WriteDictionaryEnd(hints);

                // A resident notification is a control surface: it goes away when we say so.
                writer.WriteInt32(notification.Resident ? 0 : -1);
                request = writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }

            // The returned id is what makes the next Show replace this popup instead of stacking
            // a new one, and what tells a button press which slot it belongs to.
            _ = connection.CallMethodAsync(
                    request,
                    static (Message message, object? _) => message.GetBodyReader().ReadUInt32(),
                    null
                )
                .ContinueWith(
                    task =>
                    {
                        if (task.IsCompletedSuccessfully)
                        {
                            lock (_ids)
                            {
                                _ids[slot] = task.Result;
                            }
                        }
                        else
                        {
                            _ = task.Exception;
                        }
                    },
                    TaskScheduler.Default
                );
        }
        catch (Exception)
        {
            // A missing notification is never worth an error.
        }
    }

    public void Close(int slot)
    {
        if (_connection is not { } connection) return;
        uint id;
        lock (_ids)
        {
            if (!_ids.Remove(slot, out id)) return;
        }

        try
        {
            MessageBuffer request;
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(
                    Service, ObjectPath, Interface, "CloseNotification", "u");
                writer.WriteUInt32(id);
                request = writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }

            _closing = connection.CallMethodAsync(request);
            _ = _closing.ContinueWith(
                static task => _ = task.Exception,
                TaskContinuationOptions.OnlyOnFaulted
            );
        }
        catch (Exception)
        {
            // A notification we cannot close will expire on its own eventually.
        }
    }

    /// <summary>
    ///     Close everything and wait for the last call to actually leave the socket. Close is
    ///     fire-and-forget everywhere else, which on the way out races process exit — and a
    ///     notification for an app that is gone never goes away on its own.
    /// </summary>
    public void Shutdown()
    {
        int[] slots;
        lock (_ids)
        {
            slots = [.. _ids.Keys];
        }

        foreach (int slot in slots) Close(slot);

        try
        {
            _closing?.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch (Exception)
        {
            // An unclosable notification is not worth delaying the exit over.
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _connection?.Dispose();
        _connection = null;
    }

    /// <summary>
    ///     Ask the daemon what it can do. Buttons are attached only if it lists <c>actions</c>.
    /// </summary>
    private static async Task<bool> SupportsActionsAsync(DBusConnection connection)
    {
        try
        {
            MessageBuffer request;
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(Service, ObjectPath, Interface, "GetCapabilities");
                request = writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }

            var capabilities = await connection.CallMethodAsync(
                request,
                static (Message message, object? _) => message.GetBodyReader().ReadArrayOfString(),
                null
            );
            return capabilities.Contains("actions");
        }
        catch (Exception)
        {
            return false;
        }
    }
}
