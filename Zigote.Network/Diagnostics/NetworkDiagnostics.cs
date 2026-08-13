namespace Zigote.Network;

/// <summary>
///     Aggregates per-connection <see cref="NetworkStats" /> into whole-session figures for a debug
///     overlay or
///     log line. Engine-neutral (no UI dependency) — a host can surface these through its own
///     diagnostics
///     panel. Read-only view over a live <see cref="NetworkManager" />.
/// </summary>
public sealed class NetworkDiagnostics(NetworkManager manager)
{
    public int PeerCount => manager.Connections.Count;

    public float AverageRoundTripTime
    {
        get
        {
            float sum = 0f;
            int n = 0;
            foreach (var conn in manager.Connections.Values)
            {
                sum += conn.Stats.RoundTripTime;
                n++;
            }

            return n == 0 ? 0f : sum / n;
        }
    }

    public float WorstPacketLoss
    {
        get
        {
            float worst = 0f;
            foreach (var conn in manager.Connections.Values)
                worst = Math.Max(val1: worst, val2: conn.Stats.PacketLoss);
            return worst;
        }
    }

    public float TotalSendBytesPerSecond => Sum(static s => s.SendBytesPerSecond);
    public float TotalReceiveBytesPerSecond => Sum(static s => s.ReceiveBytesPerSecond);
    public long TotalBytesSent => SumLong(static s => s.BytesSent);
    public long TotalBytesReceived => SumLong(static s => s.BytesReceived);

    public string Summary()
    {
        return
            $"peers={PeerCount} rtt={AverageRoundTripTime * 1000f:F0}ms loss={WorstPacketLoss * 100f:F1}% " +
            $"up={TotalSendBytesPerSecond / 1024f:F1}KB/s down={TotalReceiveBytesPerSecond / 1024f:F1}KB/s";
    }

    private float Sum(Func<NetworkStats, float> select)
    {
        float total = 0f;
        foreach (var conn in manager.Connections.Values) total += select(conn.Stats);
        return total;
    }

    private long SumLong(Func<NetworkStats, long> select)
    {
        long total = 0;
        foreach (var conn in manager.Connections.Values) total += select(conn.Stats);
        return total;
    }
}
