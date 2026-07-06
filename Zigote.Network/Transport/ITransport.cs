namespace Zigote.Network;

/// <summary>Which side of a transport an instance is playing.</summary>
public enum TransportRole : byte
{
    None = 0,
    Server = 1,
    Client = 2,
}

/// <summary>
///     Sink for transport events. The transport invokes these synchronously from
///     <see cref="ITransport.Update" />
///     on the caller's thread, so handlers run on the game loop — no locking required in game code.
///     The
///     <c>payload</c> span passed to <see cref="OnReceive" /> is only valid for the duration of the
///     call; copy
///     it if you need to keep it.
/// </summary>
public interface ITransportListener
{
    void OnConnected(int connectionId);
    void OnDisconnected(int connectionId, DisconnectReason reason);

    void OnReceive(int connectionId, ReadOnlySpan<byte> payload, DeliveryMethod delivery,
        int channel);

    void OnError(int connectionId, string error);
}

/// <summary>
///     The lowest networking seam: moves opaque byte payloads between endpoints with a requested
///     <see cref="DeliveryMethod" />, surfacing connect/disconnect/receive through an
///     <see cref="ITransportListener" />.
///     Implementations: reliable UDP (the workhorse), QUIC and WebSocket (encrypted /
///     browser-reachable),
///     loopback (in-process, for tests and single-process hosting), and a simulating wrapper that
///     injects
///     latency/loss. Everything above this — connection management, messaging, replication — is
///     transport
///     agnostic and works over any of them.
///     <para>
///         Single-threaded by contract: call <see cref="Update" /> once per tick to pump I/O and
///         dispatch
///         events; do not call from multiple threads. Async transports (QUIC/WebSocket) bridge their
///         background
///         I/O into <see cref="Update" /> via an internal queue.
///     </para>
/// </summary>
public interface ITransport : IDisposable
{
    TransportRole Role { get; }
    bool IsRunning { get; }

    /// <summary>The listener receiving connect/disconnect/data callbacks. Set before starting.</summary>
    ITransportListener? Listener { get; set; }

    /// <summary>Begin listening for client connections on <paramref name="port" />.</summary>
    void StartServer(int port);

    /// <summary>
    ///     Begin connecting to a server. The <see cref="ITransportListener.OnConnected" /> fires once
    ///     accepted.
    /// </summary>
    void StartClient(string host, int port);

    /// <summary>
    ///     Pump socket I/O, run reliability/keepalive/timeout, and dispatch queued events to the
    ///     listener.
    /// </summary>
    void Update(float deltaTime);

    /// <summary>Queue a payload to <paramref name="connectionId" /> with the requested delivery semantics.</summary>
    void Send(int connectionId, ReadOnlySpan<byte> payload, DeliveryMethod delivery,
        int channel = 0);

    /// <summary>Gracefully close a single connection.</summary>
    void Disconnect(int connectionId);

    /// <summary>Stop the transport, closing all connections.</summary>
    void Stop();

    /// <summary>Per-connection telemetry, or null if the connection is unknown.</summary>
    NetworkStats? GetStats(int connectionId);
}