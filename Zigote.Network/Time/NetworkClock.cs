namespace Zigote.Network;

/// <summary>
///     Estimates a shared timeline so client and server agree on "now". The server's
///     <see cref="LocalTime" />
///     is authoritative; a client periodically pings and uses the round-trip plus the server's
///     reported time to
///     track <see cref="ServerTime" /> with a smoothed offset. <see cref="RenderTime" /> trails server
///     time by
///     <see cref="NetConfig.InterpolationDelay" />, giving the snapshot interpolator a stable target.
/// </summary>
public sealed class NetworkClock(NetConfig config)
{
    private const float PingInterval = 0.2f;

    private double _offset; // serverTime - localTime
    private float _pingTimer;

    /// <summary>Local monotonic time in seconds, accumulated from the tick delta.</summary>
    public double LocalTime { get; private set; }

    /// <summary>True once at least one ping/pong has produced an offset estimate.</summary>
    public bool IsSynced { get; private set; }

    /// <summary>Smoothed round-trip time in seconds (client side).</summary>
    public float RoundTripTime { get; private set; }

    /// <summary>
    ///     Best estimate of the server's current time. On the server this equals
    ///     <see cref="LocalTime" />.
    /// </summary>
    public double ServerTime => LocalTime + _offset;

    /// <summary>Server time minus the interpolation delay — the timeline remote objects are rendered at.</summary>
    public double RenderTime => ServerTime - config.InterpolationDelay;

    public void Update(float dt)
    {
        LocalTime += dt;
        if (_pingTimer > 0f) _pingTimer -= dt;
    }

    /// <summary>Client: true when it's time to send another clock-sync ping.</summary>
    public bool ShouldPing()
    {
        return _pingTimer <= 0f;
    }

    public void SendPing(NetConnection conn)
    {
        _pingTimer = PingInterval;
        var writer = conn.BeginSend(NetChannel.TimeSync);
        writer.WriteByte((byte)Kind.Ping);
        writer.WriteDouble(LocalTime);
        conn.EndSend(DeliveryMethod.Unreliable);
    }

    internal void HandlePacket(NetConnection conn, NetReader reader)
    {
        var kind = (Kind)reader.ReadByte();
        if (kind == Kind.Ping)
        {
            var clientTime = reader.ReadDouble();
            var writer = conn.BeginSend(NetChannel.TimeSync);
            writer.WriteByte((byte)Kind.Pong);
            writer.WriteDouble(clientTime);
            writer.WriteDouble(LocalTime); // the server's authoritative time
            conn.EndSend(DeliveryMethod.Unreliable);
            return;
        }

        var sentAt = reader.ReadDouble();
        var serverTime = reader.ReadDouble();
        if (reader.Overflow) return;

        var rtt = (float)(LocalTime - sentAt);
        if (rtt < 0f) return;

        RoundTripTime = RoundTripTime <= 0f ? rtt : RoundTripTime * 0.9f + rtt * 0.1f;
        var sampleOffset = serverTime + rtt / 2.0 - LocalTime;
        _offset = IsSynced ? _offset * 0.9 + sampleOffset * 0.1 : sampleOffset;
        IsSynced = true;
    }

    private enum Kind : byte
    {
        Ping = 0,
        Pong = 1,
    }
}