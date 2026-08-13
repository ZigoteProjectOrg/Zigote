using System.Collections.Concurrent;

namespace Zigote.Network;

/// <summary>
///     An in-process transport: server and client live in the same process and exchange payloads
///     through a
///     direct queue with zero sockets. Used for headless tests, single-process listen-servers, and as
///     the
///     base another transport (e.g. <see cref="SimulatedTransport" />) decorates. Delivery is instant
///     and
///     loss-free; wrap it in a <see cref="SimulatedTransport" /> to model latency/loss.
///     <para>
///         Servers register by port in a process-wide table so a <see cref="StartClient" /> in the
///         same
///         process finds them. Tests using fixed ports should pick distinct ports or call
///         <see cref="ResetRegistry" /> in teardown. Or skip ports entirely with
///         <see cref="CreatePair" />.
///     </para>
/// </summary>
public sealed class LoopbackTransport : ITransport
{
    private static readonly object RegistryLock = new();
    private static readonly Dictionary<int, LoopbackTransport> Servers = new();

    private readonly NetConfig _config;
    private readonly ConcurrentQueue<Inbound> _inbox = new();
    private readonly Dictionary<int, Link> _links = new(); // connId -> peer link
    private int _boundPort = -1;
    private int _nextConnId = 1;

    public LoopbackTransport(NetConfig? config = null) => _config = config ?? new NetConfig();

    public TransportRole Role { get; private set; }
    public bool IsRunning { get; private set; }
    public ITransportListener? Listener { get; set; }

    public void StartServer(int port)
    {
        lock (RegistryLock)
        {
            if (Servers.ContainsKey(port))
                throw new InvalidOperationException($"Loopback port {port} already has a server.");
            Servers[port] = this;
        }

        _boundPort = port;
        Role = TransportRole.Server;
        IsRunning = true;
    }

    public void StartClient(string host, int port)
    {
        Role = TransportRole.Client;
        IsRunning = true;

        LoopbackTransport? server;
        lock (RegistryLock) Servers.TryGetValue(key: port, value: out server);

        if (server is null || !server.IsRunning)
        {
            _inbox.Enqueue(Inbound.Error(id: 1, error: $"No loopback server on port {port}"));
            _inbox.Enqueue(Inbound.Disconnected(id: 1, reason: DisconnectReason.ConnectFailed));
            IsRunning = false;
            return;
        }

        server.AcceptClient(this);
    }

    public void Update(float deltaTime)
    {
        while (_inbox.TryDequeue(out var ev))
        {
            switch (ev.Kind)
            {
                case EvKind.Connected:
                    Listener?.OnConnected(ev.ConnId);
                    break;
                case EvKind.Disconnected:
                    _links.Remove(ev.ConnId);
                    Listener?.OnDisconnected(connectionId: ev.ConnId, reason: ev.Reason);
                    break;
                case EvKind.Receive:
                    Listener?.OnReceive(
                        connectionId: ev.ConnId,
                        payload: ev.Data,
                        delivery: ev.Delivery,
                        channel: ev.Channel
                    );
                    break;
                case EvKind.Error:
                    Listener?.OnError(
                        connectionId: ev.ConnId,
                        error: ev.ErrorText ?? "loopback error"
                    );
                    break;
            }
        }
    }

    public void Send(int connectionId, ReadOnlySpan<byte> payload, DeliveryMethod delivery,
        int channel = 0)
    {
        if (!_links.TryGetValue(key: connectionId, value: out var link)) return;

        byte[] copy = payload.ToArray();
        link.Stats.PacketsSent++;
        link.Stats.BytesSent += copy.Length;
        link.Peer._links.TryGetValue(key: link.RemoteConnId, value: out var peerLink);
        if (peerLink is not null)
        {
            peerLink.Stats.PacketsReceived++;
            peerLink.Stats.BytesReceived += copy.Length;
        }

        link.Peer._inbox.Enqueue(
            Inbound.Receive(
                id: link.RemoteConnId,
                data: copy,
                delivery: delivery,
                channel: channel
            )
        );
    }

    public void Disconnect(int connectionId)
    {
        if (!_links.TryGetValue(key: connectionId, value: out var link)) return;
        link.Peer._inbox.Enqueue(
            Inbound.Disconnected(id: link.RemoteConnId, reason: DisconnectReason.RemoteClose)
        );
        _links.Remove(connectionId);
    }

    public void Stop()
    {
        foreach ((int id, _) in _links.ToArray()) Disconnect(id);
        _links.Clear();

        if (_boundPort >= 0)
        {
            lock (RegistryLock)
            {
                if (Servers.TryGetValue(key: _boundPort, value: out var s) &&
                    ReferenceEquals(objA: s, objB: this))
                    Servers.Remove(_boundPort);
            }

            _boundPort = -1;
        }

        IsRunning = false;
        Role = TransportRole.None;
    }

    public NetworkStats? GetStats(int connectionId) =>
        _links.TryGetValue(key: connectionId, value: out var l) ? l.Stats : null;

    public void Dispose() => Stop();

    /// <summary>
    ///     Create an already-connected server/client pair without using the port registry — ideal for
    ///     tests.
    /// </summary>
    public static (LoopbackTransport server, LoopbackTransport client) CreatePair(
        NetConfig? config = null)
    {
        var server = new LoopbackTransport(config) {
            Role = TransportRole.Server,
            IsRunning = true,
        };
        var client = new LoopbackTransport(config) {
            Role = TransportRole.Client,
            IsRunning = true,
        };
        server.AcceptClient(client);
        return (server, client);
    }

    /// <summary>Clears the process-wide server registry. Call in test teardown to avoid port clashes.</summary>
    public static void ResetRegistry()
    {
        lock (RegistryLock) Servers.Clear();
    }

    private void AcceptClient(LoopbackTransport client)
    {
        if (_links.Count >= _config.MaxConnections)
        {
            client._inbox.Enqueue(Inbound.Disconnected(id: 1, reason: DisconnectReason.ServerFull));
            return;
        }

        int serverSideId = _nextConnId++;
        // Server's link to the client uses serverSideId; the client side always calls the server connection 1.
        _links[serverSideId] = new Link(peer: client, remoteConnId: 1, stats: new NetworkStats());
        client._links[1] = new Link(
            peer: this,
            remoteConnId: serverSideId,
            stats: new NetworkStats()
        );

        _inbox.Enqueue(Inbound.Connected(serverSideId));
        client._inbox.Enqueue(Inbound.Connected(1));
    }

    private enum EvKind : byte
    {
        Connected,
        Disconnected,
        Receive,
        Error,
    }

    private sealed class Link(LoopbackTransport peer, int remoteConnId, NetworkStats stats)
    {
        public LoopbackTransport Peer { get; } = peer;
        public int RemoteConnId { get; } = remoteConnId;
        public NetworkStats Stats { get; } = stats;
    }

    private readonly struct Inbound
    {
        public EvKind Kind { get; private init; }
        public int ConnId { get; private init; }
        public byte[] Data { get; private init; }
        public DeliveryMethod Delivery { get; private init; }
        public int Channel { get; private init; }
        public DisconnectReason Reason { get; private init; }
        public string? ErrorText { get; private init; }

        public static Inbound Connected(int id)
        {
            return new Inbound {
                Kind = EvKind.Connected,
                ConnId = id,
                Data = [],
            };
        }

        public static Inbound Disconnected(int id, DisconnectReason reason)
        {
            return new Inbound {
                Kind = EvKind.Disconnected,
                ConnId = id,
                Data = [],
                Reason = reason,
            };
        }

        public static Inbound Receive(int id, byte[] data, DeliveryMethod delivery, int channel)
        {
            return new Inbound {
                Kind = EvKind.Receive,
                ConnId = id,
                Data = data,
                Delivery = delivery,
                Channel = channel,
            };
        }

        public static Inbound Error(int id, string error)
        {
            return new Inbound {
                Kind = EvKind.Error,
                ConnId = id,
                Data = [],
                ErrorText = error,
            };
        }
    }
}
