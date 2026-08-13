namespace Zigote.Network;

/// <summary>
///     The top-level entry point for a networked game. Wraps a chosen <see cref="ITransport" /> and
///     ties
///     together connections, typed messaging, RPC, server-authoritative replication and clock sync
///     behind one
///     <see cref="Tick" />. Run it as a server (<see cref="StartServer" />) or a client
///     (<see cref="StartClient" />), call <see cref="Tick" /> once per frame, and drive game logic
///     from the
///     <see cref="FixedTick" /> event (a constant-rate step) and the message/replication callbacks.
///     <code>
///     var net = new NetworkManager(new UdpTransport());
///     net.OnMessage&lt;ChatMessage&gt;((from, msg) =&gt; Console.WriteLine(msg.Text));
///     net.StartServer(7777);
///     // each frame:
///     net.Tick(dt);
///     </code>
/// </summary>
public sealed class NetworkManager : ITransportListener, IDisposable
{
    private readonly List<NetConnection> _connectionList = [];
    private readonly Dictionary<int, NetConnection> _connections = new();
    private readonly NetTick _fixedTick;
    private readonly MessageRouter _router;
    private readonly ITransport _transport;

    public NetworkManager(ITransport transport, NetConfig? config = null)
    {
        _transport = transport;
        Config = config ?? new NetConfig();
        Messages = new MessageRegistry();
        _router = new MessageRouter(Messages);
        Rpc = new RpcSystem(Messages);
        Clock = new NetworkClock(Config);
        _fixedTick = new NetTick(Config.TickInterval);
        _transport.Listener = this;
    }

    public NetConfig Config { get; }
    public NetworkClock Clock { get; }
    public MessageRegistry Messages { get; }
    public RpcSystem Rpc { get; }
    public ReplicationManager Replication { get; private set; } = new(false);

    public bool IsServer { get; private set; }
    public bool IsClient { get; private set; }
    public bool IsRunning => _transport.IsRunning;

    /// <summary>All active peers (server: every client; client: just the server connection).</summary>
    public IReadOnlyDictionary<int, NetConnection> Connections => _connections;

    /// <summary>Client only: the connection to the server, or null before connecting / after disconnect.</summary>
    public NetConnection? ServerConnection { get; private set; }

    /// <summary>Server only: veto a connecting client by returning false. Default accepts everyone.</summary>
    public Func<NetConnection, bool>? ApproveConnection { get; set; }

    public void Dispose() => Stop();

    // ── ITransportListener ─────────────────────────────────────────────────────
    void ITransportListener.OnConnected(int connectionId)
    {
        var conn = new NetConnection(
            id: connectionId,
            transport: _transport,
            isServerSide: IsServer
        );
        WireHandlers(conn);

        if (IsServer && ApproveConnection is not null && !ApproveConnection(conn))
        {
            conn.Disconnect();
            return;
        }

        _connections[connectionId] = conn;
        RebuildConnectionList();
        if (IsServer) Replication.OnConnectionAdded(conn);
        if (IsClient) ServerConnection = conn;

        PeerConnected?.Invoke(conn);
    }

    void ITransportListener.OnDisconnected(int connectionId, DisconnectReason reason)
    {
        if (!_connections.Remove(key: connectionId, value: out var conn)) return;

        Replication.OnConnectionRemoved(conn);
        if (ServerConnection?.Id == connectionId) ServerConnection = null;
        RebuildConnectionList();
        conn.State = ConnectionState.Disconnected;
        PeerDisconnected?.Invoke(arg1: conn, arg2: reason);
    }

    void ITransportListener.OnReceive(int connectionId, ReadOnlySpan<byte> payload,
        DeliveryMethod delivery,
        int channel)
    {
        if (_connections.TryGetValue(key: connectionId, value: out var conn))
            conn.HandleReceive(payload: payload, delivery: delivery, transportChannel: channel);
    }

    void ITransportListener.OnError(int connectionId, string error) =>
        TransportError?.Invoke(arg1: connectionId, arg2: error);

    /// <summary>A peer was established (server: a client joined; client: the server accepted us).</summary>
    public event Action<NetConnection>? PeerConnected;

    /// <summary>A peer was lost, with the reason.</summary>
    public event Action<NetConnection, DisconnectReason>? PeerDisconnected;

    public event Action<int, string>? TransportError;

    /// <summary>
    ///     Constant-rate step (<see cref="NetConfig.TickRate" /> Hz): sample input on clients, run
    ///     the sim on the server.
    /// </summary>
    public event Action<float>? FixedTick;

    public void StartServer(int port)
    {
        IsServer = true;
        IsClient = false;
        Replication = new ReplicationManager(true) { Interest = Replication.Interest };
        _transport.Listener = this;
        _transport.StartServer(port);
    }

    public void StartClient(string host, int port)
    {
        IsClient = true;
        IsServer = false;
        Replication = new ReplicationManager(false);
        _transport.Listener = this;
        _transport.StartClient(host: host, port: port);
    }

    public void Stop()
    {
        _transport.Stop();
        _connections.Clear();
        _connectionList.Clear();
        ServerConnection = null;
        Replication.Clear();
        IsServer = IsClient = false;
    }

    public void Tick(float dt)
    {
        _transport.Update(dt);
        Clock.Update(dt);
        Rpc.Update(dt);

        if (IsClient && ServerConnection is { } server && Clock.ShouldPing())
            Clock.SendPing(server);

        int steps = _fixedTick.Advance(dt);
        for (int i = 0; i < steps; i++)
        {
            FixedTick?.Invoke(_fixedTick.Interval);
            if (IsServer)
                Replication.ServerTick(connections: _connectionList, serverTime: Clock.LocalTime);
        }

        if (IsClient) Replication.InterpolateClient(Clock.RenderTime);
    }

    // ── Messaging ──────────────────────────────────────────────────────────────
    public void OnMessage<T>(Action<NetConnection, T> handler) where T : INetMessage, new() =>
        _router.Handle(handler);

    public void Send<T>(NetConnection connection, T message,
        DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        where T : INetMessage, new() =>
        _router.Send(conn: connection, message: message, delivery: delivery);

    public void SendToServer<T>(T message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        where T : INetMessage, new()
    {
        if (ServerConnection is { } server)
            _router.Send(conn: server, message: message, delivery: delivery);
    }

    public void SendToAll<T>(T message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        where T : INetMessage, new()
    {
        for (int i = 0; i < _connectionList.Count; i++)
            _router.Send(conn: _connectionList[i], message: message, delivery: delivery);
    }

    public void SendToAllExcept<T>(NetConnection except, T message,
        DeliveryMethod delivery = DeliveryMethod.ReliableOrdered) where T : INetMessage, new()
    {
        for (int i = 0; i < _connectionList.Count; i++)
        {
            if (_connectionList[i].Id != except.Id)
                _router.Send(conn: _connectionList[i], message: message, delivery: delivery);
        }
    }

    // ── RPC convenience ────────────────────────────────────────────────────────
    public Task<TResp> CallServer<TReq, TResp>(TReq request, float? timeout = null)
        where TReq : INetMessage, new()
        where TResp : INetMessage, new()
    {
        if (ServerConnection is { } server)
            return Rpc.Call<TReq, TResp>(conn: server, request: request, timeout: timeout);
        return Task.FromException<TResp>(
            new InvalidOperationException("Not connected to a server.")
        );
    }

    private void WireHandlers(NetConnection conn)
    {
        conn.On(channel: NetChannel.Message, handler: _router.Dispatch);
        conn.On(channel: NetChannel.RpcRequest, handler: Rpc.HandleRequest);
        conn.On(channel: NetChannel.RpcResponse, handler: Rpc.HandleResponse);
        conn.On(channel: NetChannel.ReplicationEvents, handler: Replication.HandleEvents);
        conn.On(channel: NetChannel.ReplicationState, handler: Replication.HandleState);
        conn.On(channel: NetChannel.TimeSync, handler: Clock.HandlePacket);
    }

    private void RebuildConnectionList()
    {
        _connectionList.Clear();
        foreach (var conn in _connections.Values) _connectionList.Add(conn);
    }
}
