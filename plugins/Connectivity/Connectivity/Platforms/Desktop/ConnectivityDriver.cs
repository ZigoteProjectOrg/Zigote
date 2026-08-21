using System.Net.NetworkInformation;

namespace Connectivity;

/// <summary>
///     Desktop implementation — <c>System.Net.NetworkInformation</c> already does this on all
///     three desktops: the interface list says what is up and what kind it is, and
///     <see cref="NetworkChange" /> raises when either moves. No native code, no polling.
/// </summary>
internal static class ConnectivityDriver
{
    private static Action<Connection>? _publish;

    public static Connection Read()
    {
        var best = ConnectionKind.None;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            // Up but with no gateway is a link to nowhere: a virtual bridge, a docker0, a NIC
            // with a link-local address and no router behind it.
            if (!nic.GetIPProperties().GatewayAddresses.Any(g => g.Address is { } a && !a.Equals(System.Net.IPAddress.Any)))
                continue;

            var kind = Classify(nic.NetworkInterfaceType);
            // Wired beats wireless beats cellular beats anything else — the best link wins the label.
            if (Rank(kind) > Rank(best)) best = kind;
        }

        return best == ConnectionKind.None ? Connection.Offline : new Connection(true, best);
    }

    /// <summary>Ranked so the ordering in <see cref="Read" /> prefers the better link.</summary>
    internal static ConnectionKind Classify(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => ConnectionKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => ConnectionKind.WiFi,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => ConnectionKind.Cellular,
        _ => ConnectionKind.Other
    };

    /// <summary>Preference order for a machine on several links at once.</summary>
    private static int Rank(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Ethernet => 4,
        ConnectionKind.WiFi => 3,
        ConnectionKind.Cellular => 2,
        ConnectionKind.Other => 1,
        _ => 0
    };

    public static void StartWatching(Action<Connection> publish)
    {
        _publish = publish;
        NetworkChange.NetworkAddressChanged += OnChanged;
        NetworkChange.NetworkAvailabilityChanged += OnChanged;
    }

    public static void StopWatching()
    {
        NetworkChange.NetworkAddressChanged -= OnChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnChanged;
        _publish = null;
    }

    private static void OnChanged(object? sender, EventArgs e) => _publish?.Invoke(Read());
}
