namespace Zigote.Network;

/// <summary>
///     Live per-connection telemetry, sampled by the transport. Mutated in place by the owner; read by
///     diagnostics overlays and bandwidth/quality logic. RTT and loss feed the retransmit timer and the
///     interpolation buffer, so these aren't just for display.
/// </summary>
public sealed class NetworkStats
{
    /// <summary>Smoothed round-trip time in seconds.</summary>
    public float RoundTripTime { get; set; }

    /// <summary>RTT variance estimate (jitter) in seconds.</summary>
    public float Jitter { get; set; }

    /// <summary>Estimated fraction of packets lost in [0, 1].</summary>
    public float PacketLoss { get; set; }

    public long PacketsSent { get; set; }
    public long PacketsReceived { get; set; }
    public long PacketsResent { get; set; }
    public long PacketsDropped { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }

    /// <summary>Bytes per second sent, averaged over the recent window.</summary>
    public float SendBytesPerSecond { get; set; }

    /// <summary>Bytes per second received, averaged over the recent window.</summary>
    public float ReceiveBytesPerSecond { get; set; }

    /// <summary>Reliable packets awaiting acknowledgement.</summary>
    public int ReliableInFlight { get; set; }

    public void Reset()
    {
        RoundTripTime = 0;
        Jitter = 0;
        PacketLoss = 0;
        PacketsSent = PacketsReceived = PacketsResent = PacketsDropped = 0;
        BytesSent = BytesReceived = 0;
        SendBytesPerSecond = ReceiveBytesPerSecond = 0;
        ReliableInFlight = 0;
    }

    public override string ToString()
    {
        return $"rtt={RoundTripTime * 1000f:F0}ms loss={PacketLoss * 100f:F1}% " +
               $"up={SendBytesPerSecond / 1024f:F1}KB/s down={ReceiveBytesPerSecond / 1024f:F1}KB/s inflight={ReliableInFlight}";
    }
}
