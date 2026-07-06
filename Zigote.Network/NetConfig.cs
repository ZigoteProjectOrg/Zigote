namespace Zigote.Network;

/// <summary>
///     Tunables shared by the transport, connection and replication layers. Construct once and hand to
///     <see cref="NetworkManager" /> (or a transport directly). Defaults target a 60 Hz action game on the
///     open internet. All time values are in seconds unless noted.
/// </summary>
public sealed class NetConfig
{
    /// <summary>
    ///     Application protocol id. Both ends must agree or the handshake is rejected with
    ///     <see cref="DisconnectReason.ProtocolMismatch" />. Bump it when the wire format changes.
    /// </summary>
    public uint ProtocolId { get; init; } = 0x5A47_4E54; // "ZGNT"

    /// <summary>Maximum number of simultaneous client connections a server accepts.</summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>
    ///     Largest UDP payload (bytes) the transport will emit without fragmenting. 1200 stays under the
    ///     common 1280-byte IPv6 path MTU after headers. Reliable messages larger than this are fragmented.
    /// </summary>
    public int Mtu { get; init; } = 1200;

    /// <summary>Simulation/replication ticks per second. Snapshots and the network clock run at this rate.</summary>
    public int TickRate { get; init; } = 60;

    /// <summary>Drop a connection after this many seconds without any received packet.</summary>
    public float ConnectionTimeout { get; init; } = 10f;

    /// <summary>Send a keepalive ping if nothing else has been sent for this long (must be &lt; timeout).</summary>
    public float KeepAliveInterval { get; init; } = 1f;

    /// <summary>How long the client waits for an accept before giving up, and the gap between connect retries.</summary>
    public float ConnectTimeout { get; init; } = 5f;

    public float ConnectRetryInterval { get; init; } = 0.25f;

    /// <summary>Base round-trip estimate (seconds) until the first ack arrives — seeds the retransmit timer.</summary>
    public float InitialRtt { get; init; } = 0.1f;

    /// <summary>Multiplier on the smoothed RTT used as the reliable-packet retransmit timeout (RTO).</summary>
    public float RetransmitRttFactor { get; init; } = 2f;

    /// <summary>Lower bound on the retransmit timeout so a tiny RTT can't cause a retransmit storm.</summary>
    public float MinRetransmitInterval { get; init; } = 0.02f;

    /// <summary>Upper bound on the retransmit timeout.</summary>
    public float MaxRetransmitInterval { get; init; } = 1f;

    /// <summary>Cap on in-flight reliable packets per connection before sends are back-pressured.</summary>
    public int MaxReliableInFlight { get; init; } = 512;

    /// <summary>
    ///     Interpolation delay (seconds) the snapshot interpolator buffers remote state by, hiding jitter.
    ///     Typically ~2 ticks. Larger = smoother but more visual latency.
    /// </summary>
    public float InterpolationDelay { get; init; } = 0.1f;

    /// <summary>Receive socket buffer size (bytes) requested from the OS.</summary>
    public int ReceiveBufferSize { get; init; } = 1 << 20;

    /// <summary>Send socket buffer size (bytes) requested from the OS.</summary>
    public int SendBufferSize { get; init; } = 1 << 20;

    /// <summary>Optional artificial network conditions applied by a simulating transport (null = none).</summary>
    public NetworkConditions? SimulatedConditions { get; init; }

    /// <summary>Seconds per tick derived from <see cref="TickRate" />.</summary>
    public float TickInterval => 1f / TickRate;

    public NetConfig Clone()
    {
        return (NetConfig)MemberwiseClone();
    }
}