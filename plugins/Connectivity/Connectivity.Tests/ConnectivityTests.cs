using System.Net.NetworkInformation;
using Xunit;

namespace Connectivity.Tests;

/// <summary>
///     The desktop classifier and the subscription bookkeeping — what a wrong answer would show
///     up in. The real interface list is whatever this machine has, so <see cref="Current" /> is
///     only checked for self-consistency.
/// </summary>
public class ConnectivityTests
{
    [Theory]
    [InlineData(NetworkInterfaceType.GigabitEthernet, ConnectionKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Wireless80211, ConnectionKind.WiFi)]
    [InlineData(NetworkInterfaceType.Wwanpp, ConnectionKind.Cellular)]
    [InlineData(NetworkInterfaceType.Ppp, ConnectionKind.Other)]
    public void Classify_MapsInterfaceTypes(NetworkInterfaceType type, ConnectionKind kind)
        => Assert.Equal(kind, ConnectivityDriver.Classify(type));

    [Fact]
    public void Current_AgreesWithItself()
    {
        var connection = ConnectivityPlugin.Current;
        Assert.Equal(connection.Online, connection.Kind != ConnectionKind.None);
    }

    [Fact]
    public void Listen_FiresOnChangeOnly_AndStopsOnDispose()
    {
        List<Connection> seen = [];
        var subscription = ConnectivityPlugin.Listen(seen.Add);

        var wifi = new Connection(true, ConnectionKind.WiFi);
        var cellular = new Connection(true, ConnectionKind.Cellular);
        ConnectivityPlugin.Publish(wifi);
        ConnectivityPlugin.Publish(wifi);            // same state — no second callback
        ConnectivityPlugin.Publish(Connection.Offline);
        ConnectivityPlugin.Publish(cellular);
        subscription.Dispose();
        ConnectivityPlugin.Publish(wifi);            // gone — not heard

        // The machine's real state seeds the dedupe, so what is asserted is the shape: no
        // repeats, everything published once, nothing after the dispose.
        Assert.Equal(cellular, seen[^1]);
        Assert.Contains(Connection.Offline, seen);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }
}
