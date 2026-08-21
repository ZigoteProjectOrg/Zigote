using Android.App;
using Android.Content;
using Android.Net;

namespace Connectivity;

/// <summary>
///     Android implementation — <see cref="ConnectivityManager" />: the default network's
///     capabilities for the reading, a default-network callback for the changes. Needs
///     <c>ACCESS_NETWORK_STATE</c> in the app manifest; without it every reading is offline.
/// </summary>
internal static class ConnectivityDriver
{
    private static Callback? _callback;

    private static ConnectivityManager? Manager
        => (ConnectivityManager?)Application.Context.GetSystemService(Context.ConnectivityService);

    public static Connection Read() => From(Manager is { } m ? m.GetNetworkCapabilities(m.ActiveNetwork) : null);

    private static Connection From(NetworkCapabilities? capabilities)
    {
        if (capabilities is null) return Connection.Offline;
        // Validated says the OS actually reached the internet through it; without it the network
        // is up but behind a captive portal, which still counts as a connection of that kind.
        if (!capabilities.HasCapability(NetCapability.Internet)) return Connection.Offline;

        var kind = capabilities.HasTransport(TransportType.Wifi) ? ConnectionKind.WiFi
            : capabilities.HasTransport(TransportType.Cellular) ? ConnectionKind.Cellular
            : capabilities.HasTransport(TransportType.Ethernet) ? ConnectionKind.Ethernet
            : ConnectionKind.Other;
        return new Connection(true, kind);
    }

    public static void StartWatching(Action<Connection> publish)
    {
        try
        {
            _callback = new Callback(publish);
            Manager?.RegisterDefaultNetworkCallback(_callback);
        }
        catch (Exception)
        {
            // Missing ACCESS_NETWORK_STATE: no events, and Read() answers offline.
            _callback = null;
        }
    }

    public static void StopWatching()
    {
        if (_callback is null) return;
        try
        {
            Manager?.UnregisterNetworkCallback(_callback);
        }
        catch (Exception)
        {
            // Already gone — unregistering twice throws on Android.
        }

        _callback = null;
    }

    private sealed class Callback(Action<Connection> publish) : ConnectivityManager.NetworkCallback
    {
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities)
            => publish(From(capabilities));

        public override void OnLost(Network network) => publish(Connection.Offline);

        public override void OnUnavailable() => publish(Connection.Offline);
    }
}
