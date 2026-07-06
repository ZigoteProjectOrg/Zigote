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

    public void Dispose()
    {
        Stop();
    }

    // ── ITransportListener ─────────────────────────────────────────────────────
    void ITransportListener.OnConnected(int connectionId)
    {
        var conn = new NetConnection(connectionId, _transport, IsServer);
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
        if (!_connections.Remove(connectionId, out var conn)) return;

        Replication.OnConnectionRemoved(conn);
        if (ServerConnection?.Id == connectionId) ServerConnection = null;
        RebuildConnectionList();
        conn.State = ConnectionState.Disconnected;
        PeerDisconnected?.Invoke(conn, reason);
    }

    void ITransportListener.OnReceive(int connectionId, ReadOnlySpan<byte> payload,
        DeliveryMethod delivery,
        int channel)
    {
        if (_connections.TryGetValue(connectionId, out var conn))
            conn.HandleReceive(payload, delivery, channel);
    }

    void ITransportListener.OnError(int connectionId, string error)
    {
        TransportError?.Invoke(connectionId, error);
    }

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
        _transport.StartClient(host, port);
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

        var steps = _fixedTick.Advance(dt);
        for (var i = 0; i < steps; i++)
        {
            FixedTick?.Invoke(_fixedTick.Interval);
            if (IsServer) Replication.ServerTick(_connectionList, Clock.LocalTime);
        }

        if (IsClient) Replication.InterpolateClient(Clock.RenderTime);
    }

    // ── Messaging ──────────────────────────────────────────────────────────────
    public void OnMessage<T>(Action<NetConnection, T> handler) where T : INetMessage, new()
    {
        _router.Handle(handler);
    }

    public void Send<T>(NetConnection connection, T message,
        DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        where T : INetMessage, new()
    {
        _router.Send(connection, message, delivery);
    }

    public void SendToServer<T>(T message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        where T : INetMessage, new()
    {
        if (ServerConnection is { } server) _router.Send(server, message, delivery);
    }

    public void SendToAll<T>(T message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        where T : INetMessage, new()
    {
        for (var i = 0; i < _connectionList.Count; i++)
            _router.Send(_connectionList[i], message, delivery);
    }

    public void SendToAllExcept<T>(NetConnection except, T message,
        DeliveryMethod delivery = DeliveryMethod.ReliableOrdered) where T : INetMessage, new()
    {
        for (var i = 0; i < _connectionList.Count; i++)
            if (_connectionList[i].Id != except.Id)
                _router.Send(_connectionList[i], message, delivery);
    }

    // ── RPC convenience ────────────────────────────────────────────────────────
    public Task<TResp> CallServer<TReq, TResp>(TReq request, float? timeout = null)
        where TReq : INetMessage, new()
        where TResp : INetMessage, new()
    {
        if (ServerConnection is { } server) return Rpc.Call<TReq, TResp>(server, request, timeout);
        return Task.FromException<TResp>(
            new InvalidOperationException("Not connected to a server.")
        );
    }

    private void WireHandlers(NetConnection conn)
    {
        conn.On(NetChannel.Message, _router.Dispatch);
        conn.On(NetChannel.RpcRequest, Rpc.HandleRequest);
        conn.On(NetChannel.RpcResponse, Rpc.HandleResponse);
        conn.On(NetChannel.ReplicationEvents, Replication.HandleEvents);
        conn.On(NetChannel.ReplicationState, Replication.HandleState);
        conn.On(NetChannel.TimeSync, Clock.HandlePacket);
    }

    private void RebuildConnectionList()
    {
        _connectionList.Clear();
        foreach (var conn in _connections.Values) _connectionList.Add(conn);
    }
}