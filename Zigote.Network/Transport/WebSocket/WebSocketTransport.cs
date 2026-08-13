using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;

namespace Zigote.Network;

// WebSocket transport over TCP — always reliable-ordered. Every DeliveryMethod maps to the same
// reliable-ordered TCP delivery; the requested method/channel ride along in a 2-byte per-message
// header so the layer above still sees what it asked for. The client uses a FIXED local id of 1 for
// the server connection and Sends to id 1 (the server's real id is unknowable to the client).
public sealed class WebSocketTransport : ITransport
{
    private const int ServerConnectionId = 1;
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly NetConfig _config;
    private readonly ConcurrentDictionary<int, Conn> _connections = new();
    private readonly ConcurrentQueue<Event> _events = new();
    private ClientWebSocket? _clientSocket;
    private CancellationTokenSource? _cts;

    private HttpListener? _listener;
    private int _nextConnectionId;

    public WebSocketTransport(NetConfig? config = null)
    {
        _config = config ?? new NetConfig();
    }

    public TransportRole Role { get; private set; } = TransportRole.None;
    public bool IsRunning { get; private set; }
    public ITransportListener? Listener { get; set; }

    public void StartServer(int port)
    {
        if (IsRunning) throw new InvalidOperationException("Transport already running.");

        Role = TransportRole.Server;
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            // The wildcard prefix needs admin/urlacl on some OSes; fall back to loopback.
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                _events.Enqueue(Event.Error(0, $"WebSocket server failed to start: {ex.Message}"));
                _listener = null;
                Role = TransportRole.None;
                return;
            }
        }

        IsRunning = true;
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    public void StartClient(string host, int port)
    {
        if (IsRunning) throw new InvalidOperationException("Transport already running.");

        Role = TransportRole.Client;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _ = ConnectAsync(host, port, _cts.Token);
    }

    public void Update(float deltaTime)
    {
        DrainEvents();
        Pump(deltaTime);
    }

    public void Send(int connectionId, ReadOnlySpan<byte> payload, DeliveryMethod delivery,
        int channel = 0)
    {
        if (!_connections.TryGetValue(connectionId, out var conn)) return;

        var frame = new byte[2 + payload.Length];
        frame[0] = (byte)delivery;
        frame[1] = (byte)channel;
        payload.CopyTo(frame.AsSpan(2));

        conn.Stats.PacketsSent++;
        conn.Stats.BytesSent += frame.Length;
        conn.LastSent = 0f;

        conn.EnqueueSend(frame);
    }

    public void Disconnect(int connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var conn)) return;
        conn.Close(WebSocketCloseStatus.NormalClosure, DisconnectReason.LocalClose);
    }

    public void Stop()
    {
        if (!IsRunning && Role == TransportRole.None) return;

        IsRunning = false;
        _cts?.Cancel();

        foreach (var conn in _connections.Values)
            conn.Close(WebSocketCloseStatus.NormalClosure, DisconnectReason.LocalClose);

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // listener already torn down
        }

        _listener = null;
        _clientSocket = null;
        Role = TransportRole.None;
    }

    public NetworkStats? GetStats(int connectionId)
    {
        return _connections.TryGetValue(connectionId, out var conn) ? conn.Stats : null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }

    private void DrainEvents()
    {
        while (_events.TryDequeue(out var evt))
            switch (evt.Kind)
            {
                case EventKind.Connected:
                    Listener?.OnConnected(evt.ConnectionId);
                    break;
                case EventKind.Disconnected:
                    if (_connections.TryRemove(evt.ConnectionId, out _))
                        Listener?.OnDisconnected(evt.ConnectionId, evt.Reason);
                    break;
                case EventKind.Receive:
                    if (_connections.TryGetValue(evt.ConnectionId, out var conn))
                    {
                        conn.Stats.PacketsReceived++;
                        conn.Stats.BytesReceived += evt.Payload!.Length;
                        conn.LastReceived = 0f;
                        var span = evt.Payload.AsSpan();
                        var delivery = (DeliveryMethod)span[0];
                        var channel = span[1];
                        Listener?.OnReceive(
                            evt.ConnectionId,
                            span[2..],
                            delivery,
                            channel
                        );
                    }

                    break;
                case EventKind.Error:
                    Listener?.OnError(evt.ConnectionId, evt.Message!);
                    break;
            }
    }

    private void Pump(float dt)
    {
        foreach (var conn in _connections.Values)
        {
            if (conn.Closing) continue;

            conn.LastReceived += dt;
            conn.LastSent += dt;

            if (conn.LastReceived >= _config.ConnectionTimeout)
            {
                conn.Close(WebSocketCloseStatus.EndpointUnavailable, DisconnectReason.Timeout);
                continue;
            }

            if (conn.LastSent >= _config.KeepAliveInterval)
            {
                conn.LastSent = 0f;
                conn.EnqueueSend(KeepAliveFrame());
                conn.Stats.PacketsSent++;
            }
        }
    }

    // Keepalive rides as a zero-length payload on a reserved channel; the receiver still resets its
    // timeout from the message arrival and the empty payload is harmless if surfaced.
    private static byte[] KeepAliveFrame()
    {
        return [(byte)DeliveryMethod.ReliableOrdered, 255];
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // listener stopped
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            if (_connections.Count >= _config.MaxConnections)
            {
                context.Response.StatusCode = 503;
                context.Response.Close();
                continue;
            }

            _ = AcceptOneAsync(context, token);
        }
    }

    private async Task AcceptOneAsync(HttpListenerContext context, CancellationToken token)
    {
        WebSocket socket;
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
            socket = wsContext.WebSocket;
        }
        catch (Exception ex)
        {
            _events.Enqueue(Event.Error(0, $"WebSocket accept failed: {ex.Message}"));
            return;
        }

        var id = Interlocked.Increment(ref _nextConnectionId);
        var conn = new Conn(id, socket, _events);

        // Server validates the client's handshake (ProtocolId) before admitting the connection.
        if (!await ReadHandshakeAsync(conn, token).ConfigureAwait(false))
        {
            await CloseSilently(socket, WebSocketCloseStatus.PolicyViolation, "protocol")
                .ConfigureAwait(false);
            return;
        }

        _connections[id] = conn;
        _events.Enqueue(Event.Connected(id));
        conn.StartSendLoop(token);
        _ = ReceiveLoopAsync(conn, token);
    }

    private async Task ConnectAsync(string host, int port, CancellationToken token)
    {
        var client = new ClientWebSocket();
        _clientSocket = client;

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.1f, _config.ConnectTimeout)));
            await client.ConnectAsync(new Uri($"ws://{host}:{port}/"), connectCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _events.Enqueue(
                Event.Error(ServerConnectionId, $"WebSocket connect failed: {ex.Message}")
            );
            _events.Enqueue(Event.Disconnected(ServerConnectionId, DisconnectReason.ConnectFailed));
            return;
        }

        var conn = new Conn(ServerConnectionId, client, _events);

        // Client sends the ProtocolId as the first 4 bytes after connect.
        var handshake = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(handshake, _config.ProtocolId);
        try
        {
            await client.SendAsync(
                handshake,
                WebSocketMessageType.Binary,
                true,
                token
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _events.Enqueue(
                Event.Error(ServerConnectionId, $"WebSocket handshake send failed: {ex.Message}")
            );
            _events.Enqueue(
                Event.Disconnected(ServerConnectionId, DisconnectReason.TransportError)
            );
            return;
        }

        _connections[ServerConnectionId] = conn;
        _events.Enqueue(Event.Connected(ServerConnectionId));
        conn.StartSendLoop(token);
        _ = ReceiveLoopAsync(conn, token);
    }

    private async Task<bool> ReadHandshakeAsync(Conn conn, CancellationToken token)
    {
        var first = await conn.ReceiveMessageAsync(token).ConfigureAwait(false);
        if (first is null || first.Length < 4) return false;

        var protocol = BinaryPrimitives.ReadUInt32LittleEndian(first);
        if (protocol != _config.ProtocolId)
        {
            _events.Enqueue(Event.Disconnected(conn.Id, DisconnectReason.ProtocolMismatch));
            return false;
        }

        return true;
    }

    private async Task ReceiveLoopAsync(Conn conn, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await conn.ReceiveMessageAsync(token).ConfigureAwait(false);
                if (message is null)
                {
                    _events.Enqueue(
                        Event.Disconnected(
                            conn.Id,
                            conn.CloseReason ?? DisconnectReason.RemoteClose
                        )
                    );
                    return;
                }

                if (message.Length < 2)
                    continue; // malformed / sub-header (keepalive arrives length 2)
                _events.Enqueue(Event.Receive(conn.Id, message));
            }
        }
        catch (OperationCanceledException)
        {
            _events.Enqueue(Event.Disconnected(conn.Id, DisconnectReason.LocalClose));
        }
        catch (Exception ex)
        {
            _events.Enqueue(Event.Error(conn.Id, ex.Message));
            _events.Enqueue(Event.Disconnected(conn.Id, DisconnectReason.TransportError));
        }
    }

    private static async Task CloseSilently(WebSocket socket, WebSocketCloseStatus status,
        string description)
    {
        try
        {
            await socket.CloseAsync(status, description, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }

        socket.Dispose();
    }

    private sealed class Conn
    {
        private readonly ConcurrentQueue<Event> _events;
        private readonly ConcurrentQueue<byte[]> _outgoing = new();
        private readonly SemaphoreSlim _sendSignal = new(0);

        public Conn(int id, WebSocket socket, ConcurrentQueue<Event> events)
        {
            Id = id;
            Socket = socket;
            _events = events;
        }

        public int Id { get; }
        public WebSocket Socket { get; }
        public NetworkStats Stats { get; } = new();
        public float LastReceived { get; set; }
        public float LastSent { get; set; }
        public bool Closing { get; private set; }
        public DisconnectReason? CloseReason { get; private set; }

        public void EnqueueSend(byte[] frame)
        {
            if (Closing) return;
            _outgoing.Enqueue(frame);
            _sendSignal.Release();
        }

        public void StartSendLoop(CancellationToken token)
        {
            _ = SendLoopAsync(token);
        }

        private async Task SendLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && !Closing)
                {
                    await _sendSignal.WaitAsync(token).ConfigureAwait(false);
                    while (_outgoing.TryDequeue(out var frame))
                    {
                        if (Socket.State != WebSocketState.Open) return;
                        await Socket.SendAsync(
                            frame,
                            WebSocketMessageType.Binary,
                            true,
                            token
                        ).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _events.Enqueue(Event.Error(Id, ex.Message));
            }
        }

        public async Task<byte[]?> ReceiveMessageAsync(CancellationToken token)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                var written = 0;
                while (true)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await Socket.ReceiveAsync(
                                new ArraySegment<byte>(buffer, written, buffer.Length - written),
                                token
                            )
                            .ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        CloseReason ??= DisconnectReason.TransportError;
                        return null;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CloseReason ??= DisconnectReason.RemoteClose;
                        return null;
                    }

                    written += result.Count;
                    if (result.EndOfMessage) break;

                    if (written >= buffer.Length)
                    {
                        if (buffer.Length * 2 > MaxMessageBytes)
                        {
                            CloseReason ??= DisconnectReason.TransportError;
                            return null;
                        }

                        var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        Array.Copy(buffer, bigger, written);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                    }
                }

                var message = new byte[written];
                Array.Copy(buffer, message, written);
                return message;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Close(WebSocketCloseStatus status, DisconnectReason reason)
        {
            if (Closing) return;
            Closing = true;
            CloseReason = reason;
            _events.Enqueue(Event.Disconnected(Id, reason));
            _sendSignal.Release();
            _ = CloseInternalAsync(status);
        }

        private async Task CloseInternalAsync(WebSocketCloseStatus status)
        {
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.CloseAsync(status, string.Empty, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch
            {
                // best-effort graceful close; abort below
            }

            try
            {
                Socket.Abort();
                Socket.Dispose();
            }
            catch
            {
                // already disposed
            }
        }
    }

    private enum EventKind : byte
    {
        Connected,
        Disconnected,
        Receive,
        Error,
    }

    private readonly struct Event
    {
        public EventKind Kind { get; private init; }
        public int ConnectionId { get; private init; }
        public DisconnectReason Reason { get; private init; }
        public byte[]? Payload { get; private init; }
        public string? Message { get; private init; }

        public static Event Connected(int id)
        {
            return new Event {
                Kind = EventKind.Connected,
                ConnectionId = id,
            };
        }

        public static Event Disconnected(int id, DisconnectReason reason)
        {
            return new Event {
                Kind = EventKind.Disconnected,
                ConnectionId = id,
                Reason = reason,
            };
        }

        public static Event Receive(int id, byte[] payload)
        {
            return new Event {
                Kind = EventKind.Receive,
                ConnectionId = id,
                Payload = payload,
            };
        }

        public static Event Error(int id, string error)
        {
            return new Event {
                Kind = EventKind.Error,
                ConnectionId = id,
                Message = error,
            };
        }
    }
}
