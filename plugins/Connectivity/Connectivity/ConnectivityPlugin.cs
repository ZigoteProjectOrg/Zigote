namespace Connectivity;

/// <summary>How the device is reaching the network.</summary>
public enum ConnectionKind
{
    /// <summary>No usable connection.</summary>
    None,

    /// <summary>Wi-Fi.</summary>
    WiFi,

    /// <summary>A wired link.</summary>
    Ethernet,

    /// <summary>A mobile network — the one worth asking the user about before a big download.</summary>
    Cellular,

    /// <summary>Connected over something else: VPN, Bluetooth tether, an interface the OS will not classify.</summary>
    Other
}

/// <summary>
///     Whether the device has a network and what kind.
///     <para>
///         <c>Online</c> means "there is a route out", not "the internet answers" — a captive
///         portal, a dead uplink or a firewall all still read as online. Reachability of a
///         particular server is a request that either works or does not, and that is
///         Zigote.Http's business, not this plugin's.
///     </para>
/// </summary>
public readonly record struct Connection(bool Online, ConnectionKind Kind)
{
    /// <summary>Nothing connected.</summary>
    public static readonly Connection Offline = new(false, ConnectionKind.None);

    /// <summary>Metered by default — the point where an app asks before spending the user's data.</summary>
    public bool IsCellular => Kind == ConnectionKind.Cellular;
}

/// <summary>
///     Connectivity — current network state and change notifications, the
///     <c>connectivity_plus</c> slot from the plugin roadmap. Static, nothing to register with
///     <c>PluginHost</c>: the platform watcher starts with the first listener and stops with the
///     last.
///     <para>
///         Callbacks arrive on whatever thread the OS reports on — post to the app thread before
///         touching widgets.
///     </para>
/// </summary>
public static class ConnectivityPlugin
{
    private static readonly Lock Gate = new();
    private static readonly List<Action<Connection>> Listeners = [];
    private static Connection _last;

    /// <summary>Read the connection now.</summary>
    public static Connection Current
    {
        get
        {
            try
            {
                return ConnectivityDriver.Read();
            }
            catch (Exception)
            {
                return Connection.Offline;
            }
        }
    }

    /// <summary>
    ///     Watch for changes. The handler fires on every change — never twice for the same
    ///     state. Dispose to stop; the last dispose stops the platform watcher too.
    /// </summary>
    public static IDisposable Listen(Action<Connection> onChange)
    {
        lock (Gate)
        {
            Listeners.Add(onChange);
            if (Listeners.Count == 1)
            {
                _last = Current;
                ConnectivityDriver.StartWatching(Publish);
            }
        }

        return new Subscription(onChange);
    }

    /// <summary>What the platform watcher calls. Silent when nothing actually changed.</summary>
    internal static void Publish(Connection connection)
    {
        Action<Connection>[] listeners;
        lock (Gate)
        {
            if (connection == _last) return;
            _last = connection;
            listeners = Listeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(connection);
            }
            catch (Exception)
            {
                // One bad handler does not deafen the others.
            }
        }
    }

    private static void Remove(Action<Connection> handler)
    {
        lock (Gate)
        {
            if (!Listeners.Remove(handler) || Listeners.Count > 0) return;
            ConnectivityDriver.StopWatching();
        }
    }

    private sealed class Subscription(Action<Connection> handler) : IDisposable
    {
        private Action<Connection>? _handler = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _handler, null) is { } h) Remove(h);
        }
    }
}
