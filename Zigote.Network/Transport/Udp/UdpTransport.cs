using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace Zigote.Network;

/// <summary>
///     The reference reliable-UDP transport: one non-blocking <see cref="Socket" /> with one
///     <see cref="ReliableEndpoint" /> per remote peer. A tiny control sub-protocol (distinct from the
///     endpoint's app datagrams) handles connect/accept/disconnect and protocol-id validation;
///     everything
///     else flows through the per-peer reliability layer. Single-threaded by contract — all callbacks
///     fire
///     synchronously from <see cref="Update" />.
/// </summary>
public sealed class UdpTransport : ITransport
{
    // Outer framing byte that separates endpoint traffic from the handshake/control sub-protocol so they
    // never collide with app channels.
    private const byte FrameEndpoint = 0;
    private const byte FrameControl = 1;

    // On the client side the server connection always has this fixed local id (the server's real id is not
    // knowable to the client). Send(1, ...) targets the server.
    private const int ServerConnectionId = 1;
    private readonly Dictionary<IPEndPoint, int> _byEndpoint = new();

    private readonly NetConfig _config;
    private readonly Queue<int> _disconnectScratch = new();
    private readonly Dictionary<int, Peer> _peers = new();
    private readonly NetReader _reader = new();
    private readonly byte[] _recvBuffer = new byte[2048];
    private readonly NetWriter _writer = new();
    private bool _clientConnecting;
    private float _connectElapsed;
    private float _connectRetryTimer;
    private int _nextConnectionId = 1;

    // Client connect state.
    private IPEndPoint? _serverEndpoint;

    private Socket? _socket;

    public UdpTransport(NetConfig? config = null) => _config = config ?? new NetConfig();

    public TransportRole Role { get; private set; } = TransportRole.None;
    public bool IsRunning { get; private set; }
    public ITransportListener? Listener { get; set; }

    public void StartServer(int port)
    {
        OpenSocket(local: new IPEndPoint(address: IPAddress.Any, port: port), bind: true);
        Role = TransportRole.Server;
        IsRunning = true;
    }

    public void StartClient(string host, int port)
    {
        OpenSocket(local: new IPEndPoint(address: IPAddress.Any, port: 0), bind: false);
        Role = TransportRole.Client;
        IsRunning = true;

        var address = ResolveHost(host);
        _serverEndpoint = new IPEndPoint(address: address, port: port);
        _clientConnecting = true;
        _connectElapsed = 0f;
        _connectRetryTimer = 0f;
        SendControl(to: _serverEndpoint, control: Control.ConnectRequest, withProtocol: true);
    }

    public void Update(float deltaTime)
    {
        if (!IsRunning || _socket is null) return;

        DrainSocket();
        PumpClientConnect(deltaTime);

        foreach (var peer in _peers.Values) peer.Endpoint.Update(deltaTime);

        // Timeout sweep — collect first, then fire, so the dictionary isn't mutated during iteration.
        foreach ((int id, var peer) in _peers)
        {
            if (peer.Endpoint.TimeSinceLastReceive >= _config.ConnectionTimeout)
                _disconnectScratch.Enqueue(id);
        }

        while (_disconnectScratch.Count > 0)
        {
            CloseConnection(
                connectionId: _disconnectScratch.Dequeue(),
                reason: DisconnectReason.Timeout,
                notifyRemote: false
            );
        }
    }

    public void Send(int connectionId, ReadOnlySpan<byte> payload, DeliveryMethod delivery,
        int channel = 0)
    {
        if (!_peers.TryGetValue(key: connectionId, value: out var peer))
        {
            Listener?.OnError(connectionId: connectionId, error: "send to unknown connection");
            return;
        }

        if (!delivery.IsReliable() && payload.Length > _config.Mtu)
        {
            Listener?.OnError(
                connectionId: connectionId,
                error: "unreliable payload exceeds MTU; dropped"
            );
            peer.Stats.PacketsDropped++;
            return;
        }

        peer.Endpoint.SendMessage(payload: payload, delivery: delivery, channel: (byte)channel);
    }

    public void Disconnect(int connectionId) => CloseConnection(
        connectionId: connectionId,
        reason: DisconnectReason.LocalClose,
        notifyRemote: true
    );

    public void Stop()
    {
        if (!IsRunning) return;

        foreach (var (_, peer) in _peers)
            SendControl(to: peer.RemoteEndpoint, control: Control.Disconnect, withProtocol: false);

        _peers.Clear();
        _byEndpoint.Clear();
        _clientConnecting = false;

        _socket?.Close();
        _socket = null;
        IsRunning = false;
        Role = TransportRole.None;
    }

    public NetworkStats? GetStats(int connectionId) =>
        _peers.TryGetValue(key: connectionId, value: out var peer) ? peer.Stats : null;

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- socket

    private void OpenSocket(IPEndPoint local, bool bind)
    {
        var socket =
            new Socket(
                addressFamily: AddressFamily.InterNetwork,
                socketType: SocketType.Dgram,
                protocolType: ProtocolType.Udp
            ) {
                Blocking = false,
                ReceiveBufferSize = _config.ReceiveBufferSize,
                SendBufferSize = _config.SendBufferSize,
            };

        // Windows only: SIO_UDP_CONNRESET — ignore ICMP port-unreachable so a single closed
        // peer can't kill the receive loop. Non-Windows throws PlatformNotSupportedException
        // (and doesn't surface those resets as socket errors in the first place).
        if (OperatingSystem.IsWindows())
        {
            try
            {
                socket.IOControl(
                    ioControlCode: unchecked((int)0x9800000C),
                    optionInValue: [0, 0, 0, 0],
                    optionOutValue: null
                );
            }
            catch (SocketException) { }
        }

        if (bind) socket.Bind(local);
        _socket = socket;
    }

    private void DrainSocket()
    {
        var socket = _socket!;
        EndPoint from = new IPEndPoint(address: IPAddress.Any, port: 0);

        while (true)
        {
            int received;
            try
            {
                if (socket.Available <= 0) break;
                received = socket.ReceiveFrom(
                    buffer: _recvBuffer,
                    socketFlags: SocketFlags.None,
                    remoteEP: ref from
                );
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock)
            {
                break;
            }
            catch (SocketException ex)
            {
                Listener?.OnError(
                    connectionId: 0,
                    error: $"socket receive error: {ex.SocketErrorCode}"
                );
                break;
            }

            if (received <= 0) continue;
            RouteDatagram(
                from: (IPEndPoint)from,
                datagram: _recvBuffer.AsSpan(start: 0, length: received)
            );
        }
    }

    private void RouteDatagram(IPEndPoint from, ReadOnlySpan<byte> datagram)
    {
        byte frame = datagram[0];
        var body = datagram[1..];

        if (frame == FrameControl)
        {
            HandleControl(from: from, body: body);
            return;
        }

        if (frame != FrameEndpoint) return;

        if (_byEndpoint.TryGetValue(key: from, value: out int id) &&
            _peers.TryGetValue(key: id, value: out var peer))
            peer.Endpoint.ReceiveRaw(body);
        // Endpoint traffic from an unknown peer (e.g. a late datagram after disconnect) is ignored.
    }

    // ---------------------------------------------------------------- control sub-protocol

    private void HandleControl(IPEndPoint from, ReadOnlySpan<byte> body)
    {
        _reader.SetSource(body);
        var control = (Control)_reader.ReadByte();
        if (_reader.Overflow) return;

        switch (control)
        {
            case Control.ConnectRequest when Role == TransportRole.Server:
                HandleConnectRequest(from);
                break;

            case Control.ConnectAccept when Role == TransportRole.Client && _clientConnecting:
                CompleteClientConnect(from);
                break;

            case Control.ConnectReject when Role == TransportRole.Client && _clientConnecting:
                _clientConnecting = false;
                Listener?.OnDisconnected(
                    connectionId: ServerConnectionId,
                    reason: DisconnectReason.ProtocolMismatch
                );
                break;

            case Control.Disconnect:
                if (_byEndpoint.TryGetValue(key: from, value: out int id))
                {
                    CloseConnection(
                        connectionId: id,
                        reason: DisconnectReason.RemoteClose,
                        notifyRemote: false
                    );
                }

                break;
        }
    }

    private void HandleConnectRequest(IPEndPoint from)
    {
        if (_byEndpoint.TryGetValue(key: from, value: out int existing))
        {
            // Retried request from an already-accepted peer — re-ack so a lost accept is recovered.
            SendControl(
                to: _peers[existing].RemoteEndpoint,
                control: Control.ConnectAccept,
                withProtocol: false
            );
            return;
        }

        uint protocol = _reader.ReadUInt32();
        if (_reader.Overflow || protocol != _config.ProtocolId)
        {
            SendControl(to: from, control: Control.ConnectReject, withProtocol: false);
            return;
        }

        if (_peers.Count >= _config.MaxConnections)
        {
            SendControl(to: from, control: Control.ConnectReject, withProtocol: false);
            return;
        }

        int id = _nextConnectionId++;
        AddPeer(id: id, endpoint: from);
        SendControl(to: from, control: Control.ConnectAccept, withProtocol: false);
        Listener?.OnConnected(id);
    }

    private void CompleteClientConnect(IPEndPoint from)
    {
        _clientConnecting = false;
        AddPeer(id: ServerConnectionId, endpoint: from);
        Listener?.OnConnected(ServerConnectionId);
    }

    private void PumpClientConnect(float dt)
    {
        if (!_clientConnecting || _serverEndpoint is null) return;

        _connectElapsed += dt;
        if (_connectElapsed >= _config.ConnectTimeout)
        {
            _clientConnecting = false;
            Listener?.OnDisconnected(
                connectionId: ServerConnectionId,
                reason: DisconnectReason.ConnectFailed
            );
            return;
        }

        _connectRetryTimer += dt;
        if (_connectRetryTimer >= _config.ConnectRetryInterval)
        {
            _connectRetryTimer = 0f;
            SendControl(to: _serverEndpoint, control: Control.ConnectRequest, withProtocol: true);
        }
    }

    private void SendControl(IPEndPoint to, Control control, bool withProtocol)
    {
        _writer.Clear();
        _writer.WriteByte(FrameControl);
        _writer.WriteByte((byte)control);
        if (withProtocol) _writer.WriteUInt32(_config.ProtocolId);
        SendFramed(to: to, framed: _writer.AsSpan());
    }

    // ---------------------------------------------------------------- peers

    private void AddPeer(int id, IPEndPoint endpoint)
    {
        var remote = new IPEndPoint(
            address: endpoint.Address,
            port: endpoint.Port
        ); // own a stable copy as a dictionary key
        var peer = new Peer(
            remote: remote,
            endpoint: new ReliableEndpoint(
                config: _config,
                sendRaw: mem => SendEndpoint(to: remote, body: mem.Span)
            )
        );
        peer.Endpoint.MessageReceived += (payload, delivery, channel) =>
            Listener?.OnReceive(
                connectionId: id,
                payload: payload,
                delivery: delivery,
                channel: channel
            );

        _peers[id] = peer;
        _byEndpoint[remote] = id;
    }

    private void CloseConnection(int connectionId, DisconnectReason reason, bool notifyRemote)
    {
        if (!_peers.TryGetValue(key: connectionId, value: out var peer)) return;

        if (notifyRemote)
            SendControl(to: peer.RemoteEndpoint, control: Control.Disconnect, withProtocol: false);

        _peers.Remove(connectionId);
        _byEndpoint.Remove(peer.RemoteEndpoint);
        Listener?.OnDisconnected(connectionId: connectionId, reason: reason);
    }

    // Endpoint datagrams (from ReliableEndpoint) get the FrameEndpoint prefix prepended here.
    private void SendEndpoint(IPEndPoint to, ReadOnlySpan<byte> body)
    {
        var socket = _socket;
        if (socket is null) return;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(body.Length + 1);
        buffer[0] = FrameEndpoint;
        body.CopyTo(buffer.AsSpan(1));
        TrySendTo(socket: socket, data: buffer.AsSpan(start: 0, length: body.Length + 1), to: to);
        ArrayPool<byte>.Shared.Return(buffer);
    }

    // Control datagrams already carry their FrameControl byte as the first written byte.
    private void SendFramed(IPEndPoint to, ReadOnlySpan<byte> framed)
    {
        if (_socket is { } socket) TrySendTo(socket: socket, data: framed, to: to);
    }

    private void TrySendTo(Socket socket, ReadOnlySpan<byte> data, IPEndPoint to)
    {
        try
        {
            socket.SendTo(buffer: data, socketFlags: SocketFlags.None, remoteEP: to);
        }
        catch (SocketException)
        {
            /* best-effort UDP send */
        }
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(ipString: host, address: out var ip)) return ip;
        var entries = Dns.GetHostAddresses(host);
        foreach (var entry in entries)
        {
            if (entry.AddressFamily == AddressFamily.InterNetwork)
                return entry;
        }

        return IPAddress.Loopback;
    }

    private enum Control : byte
    {
        ConnectRequest = 0,
        ConnectAccept = 1,
        ConnectReject = 2,
        Disconnect = 3,
    }

    private sealed class Peer(IPEndPoint remote, ReliableEndpoint endpoint)
    {
        public readonly ReliableEndpoint Endpoint = endpoint;
        public readonly IPEndPoint RemoteEndpoint = remote;
        public NetworkStats Stats => Endpoint.Stats;
    }
}
