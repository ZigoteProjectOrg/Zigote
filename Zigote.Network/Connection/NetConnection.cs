namespace Zigote.Network;

/// <summary>
///     One logical peer on a transport — the server's view of a client, or the client's view of the
///     server.
///     Multiplexes the subsystems (messages, RPC, replication, time-sync) that share the underlying
///     transport
///     connection: outgoing payloads are framed with a leading <see cref="NetChannel" /> byte, and
///     incoming
///     payloads are dispatched to the handler registered for their channel. Owns reusable read/write
///     buffers
///     so steady-state send/receive does not allocate.
/// </summary>
public sealed class NetConnection
{
    private static readonly NetworkStats Empty = new();
    private readonly Dictionary<byte, Action<NetConnection, NetReader>> _handlers = new();
    private readonly NetReader _recv = new();
    private readonly NetWriter _send = new(1024);

    private readonly ITransport _transport;

    internal NetConnection(int id, ITransport transport, bool isServerSide)
    {
        Id = id;
        _transport = transport;
        IsServerSide = isServerSide;
    }

    /// <summary>Transport connection id.</summary>
    public int Id { get; }

    /// <summary>True when this is the authoritative server's view of a remote client.</summary>
    public bool IsServerSide { get; }

    public ConnectionState State { get; internal set; } = ConnectionState.Connected;

    /// <summary>Free slot for game state — e.g. the player object owned by this connection.</summary>
    public object? UserData { get; set; }

    /// <summary>Live transport telemetry for this peer.</summary>
    public NetworkStats Stats => _transport.GetStats(Id) ?? Empty;

    /// <summary>Round-trip time in seconds, from the transport's estimate.</summary>
    public float RoundTripTime => Stats.RoundTripTime;

    /// <summary>
    ///     Begin framing a payload on <paramref name="channel" />. Write the body into the returned
    ///     writer, then call
    ///     <see cref="EndSend" />.
    /// </summary>
    internal NetWriter BeginSend(NetChannel channel)
    {
        _send.Clear();
        _send.WriteByte((byte)channel);
        return _send;
    }

    internal void EndSend(DeliveryMethod delivery, int transportChannel = 0)
    {
        _transport.Send(
            Id,
            _send.AsSpan(),
            delivery,
            transportChannel
        );
    }

    /// <summary>Register the handler invoked when a payload arrives on <paramref name="channel" />.</summary>
    internal void On(NetChannel channel, Action<NetConnection, NetReader> handler)
    {
        _handlers[(byte)channel] = handler;
    }

    internal void HandleReceive(ReadOnlySpan<byte> payload, DeliveryMethod delivery,
        int transportChannel)
    {
        if (payload.Length == 0) return;
        _recv.SetSource(payload);
        var channel = _recv.ReadByte();
        if (_handlers.TryGetValue(channel, out var handler)) handler(this, _recv);
    }

    /// <summary>Gracefully close this connection.</summary>
    public void Disconnect()
    {
        State = ConnectionState.Disconnecting;
        _transport.Disconnect(Id);
    }
}
