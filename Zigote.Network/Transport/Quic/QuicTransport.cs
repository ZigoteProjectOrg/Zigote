using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

#pragma warning disable CA1416 // QUIC support is probed at runtime via Is*.IsSupported before any use.

namespace Zigote.Network;

/// <summary>
///     QUIC transport over .NET's <see cref="System.Net.Quic" /> (GA in .NET 10). Encrypted, reliable
///     and
///     ordered: the public QUIC API exposes no unreliable datagram support, so every
///     <see cref="DeliveryMethod" />
///     is carried over a single per-connection bidirectional stream as reliable-ordered. Framing on
///     that stream
///     is length-prefixed: [varint length][1 byte delivery][1 byte channel][payload]. Background async
///     loops
///     bridge into <see cref="Update" /> through a thread-safe queue; callbacks fire only from Update.
/// </summary>
public sealed class QuicTransport(NetConfig? config = null) : ITransport
{
    // Client side uses a fixed local id of 1 for "the server" connection; Send to 1 to reach the server.
    private const int ServerConnectionId = 1;

    // Reserved delivery codes ride the same stream as app frames but are filtered out before dispatch.
    private const byte HandshakeDelivery = 0xFE;
    private const byte KeepAliveDelivery = 0xFF;

    // Hard cap on a single framed message to bound allocation from a malformed/hostile length prefix.
    private const long MaxFrameLength = 64 * 1024 * 1024;
    private static readonly SslApplicationProtocol Alpn = new("zigote");

    private readonly NetConfig _config = config ?? new NetConfig();
    private readonly ConcurrentDictionary<int, Conn> _conns = new();
    private readonly ConcurrentQueue<Event> _events = new();
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;

    private QuicListener? _listener;
    private int _nextConnectionId;
    private X509Certificate2? _serverCert;

    public TransportRole Role { get; private set; } = TransportRole.None;
    public bool IsRunning { get; private set; }
    public ITransportListener? Listener { get; set; }

    public void StartServer(int port)
    {
        if (IsRunning) return;

        if (!QuicListener.IsSupported)
        {
            Listener?.OnError(0, "QUIC is not supported on this platform (MsQuic unavailable).");
            return;
        }

        Role = TransportRole.Server;
        _cts = new CancellationTokenSource();
        _nextConnectionId = 0;
        _ = RunServerAsync(port, _cts.Token);
    }

    public void StartClient(string host, int port)
    {
        if (IsRunning) return;

        if (!QuicConnection.IsSupported)
        {
            Listener?.OnError(0, "QUIC is not supported on this platform (MsQuic unavailable).");
            return;
        }

        Role = TransportRole.Client;
        _cts = new CancellationTokenSource();
        _ = RunClientAsync(host, port, _cts.Token);
    }

    public void Update(float deltaTime)
    {
        while (_events.TryDequeue(out var ev))
            switch (ev.Kind)
            {
                case EventKind.Connected:
                    Listener?.OnConnected(ev.ConnectionId);
                    break;
                case EventKind.Disconnected:
                    Listener?.OnDisconnected(ev.ConnectionId, ev.Reason);
                    break;
                case EventKind.Receive:
                    Listener?.OnReceive(
                        ev.ConnectionId,
                        ev.Payload!,
                        ev.Delivery,
                        ev.Channel
                    );
                    break;
                case EventKind.Error:
                    Listener?.OnError(ev.ConnectionId, ev.Error ?? "transport error");
                    break;
            }

        foreach (var conn in _conns.Values)
            TickConnection(conn, deltaTime);
    }

    public void Send(int connectionId, ReadOnlySpan<byte> payload, DeliveryMethod delivery,
        int channel = 0)
    {
        if (!_conns.TryGetValue(connectionId, out var conn) || conn.Closing) return;
        SendFrame(
            conn,
            payload,
            (byte)delivery,
            (byte)channel
        );
    }

    public void Disconnect(int connectionId)
    {
        if (_conns.TryGetValue(connectionId, out var conn))
            CloseConnection(conn, DisconnectReason.LocalClose, true);
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning && Role == TransportRole.None) return;

            _cts?.Cancel();

            foreach (var conn in _conns.Values)
                CloseConnection(conn, DisconnectReason.LocalClose, false);

            _ = DisposeListenerAsync(_listener);
            _listener = null;

            _serverCert?.Dispose();
            _serverCert = null;

            _cts?.Dispose();
            _cts = null;

            IsRunning = false;
            Role = TransportRole.None;
        }
    }

    public NetworkStats? GetStats(int connectionId)
    {
        return _conns.TryGetValue(connectionId, out var conn) ? conn.Stats : null;
    }

    public void Dispose()
    {
        Stop();
    }

    private void TickConnection(Conn conn, float dt)
    {
        if (conn.Closing) return;

        conn.TimeSinceReceive += dt;
        conn.TimeSinceSend += dt;
        UpdateBandwidth(conn, dt);

        if (conn.TimeSinceReceive >= _config.ConnectionTimeout)
        {
            CloseConnection(conn, DisconnectReason.Timeout, true);
            return;
        }

        if (conn.TimeSinceSend >= _config.KeepAliveInterval)
            SendFrame(
                conn,
                ReadOnlySpan<byte>.Empty,
                KeepAliveDelivery,
                0
            );
    }

    private static void UpdateBandwidth(Conn conn, float dt)
    {
        conn.WindowTime += dt;
        if (conn.WindowTime < 1f) return;

        conn.Stats.SendBytesPerSecond = conn.WindowBytesSent / conn.WindowTime;
        conn.Stats.ReceiveBytesPerSecond = conn.WindowBytesReceived / conn.WindowTime;
        conn.WindowBytesSent = 0;
        conn.WindowBytesReceived = 0;
        conn.WindowTime = 0;
    }

    private void SendFrame(Conn conn, ReadOnlySpan<byte> payload, byte delivery, byte channel)
    {
        var stream = conn.Stream;
        if (stream is null) return;

        var headerLen = VarUIntByteLength((uint)payload.Length) + 2;
        var frame = new byte[headerLen + payload.Length];
        var offset = WriteVarUInt(frame, (uint)payload.Length);
        frame[offset++] = delivery;
        frame[offset++] = channel;
        payload.CopyTo(frame.AsSpan(offset));

        conn.TimeSinceSend = 0;
        conn.Stats.PacketsSent++;
        conn.Stats.BytesSent += frame.Length;
        conn.WindowBytesSent += frame.Length;

        _ = WriteFrameAsync(conn, frame);
    }

    private async Task WriteFrameAsync(Conn conn, byte[] frame)
    {
        try
        {
            await conn.WriteLock.WaitAsync(conn.Token).ConfigureAwait(false);
            try
            {
                await conn.Stream!.WriteAsync(frame, conn.Token).ConfigureAwait(false);
                await conn.Stream!.FlushAsync(conn.Token).ConfigureAwait(false);
            }
            finally
            {
                conn.WriteLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            EnqueueDisconnect(conn, DisconnectReason.TransportError, ex.Message);
        }
    }

    // ── Server ──────────────────────────────────────────────────────────────

    private async Task RunServerAsync(int port, CancellationToken token)
    {
        try
        {
            _serverCert = CreateDevCertificate();

            var options = new QuicListenerOptions {
                ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, port),
                ApplicationProtocols = [Alpn],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(
                    new QuicServerConnectionOptions {
                        DefaultStreamErrorCode = 0,
                        DefaultCloseErrorCode = 0,
                        ServerAuthenticationOptions = new SslServerAuthenticationOptions {
                            ApplicationProtocols = [Alpn],
                            ServerCertificate = _serverCert,
                        },
                    }
                ),
            };

            _listener = await QuicListener.ListenAsync(options, token).ConfigureAwait(false);
            IsRunning = true;

            while (!token.IsCancellationRequested)
            {
                var quic = await _listener.AcceptConnectionAsync(token).ConfigureAwait(false);

                if (_conns.Count >= _config.MaxConnections)
                {
                    _ = quic.CloseAsync(0);
                    continue;
                }

                _ = AcceptConnectionAsync(quic, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            IsRunning = false;
            _events.Enqueue(Event.MakeError(0, $"QUIC server failed: {ex.Message}"));
        }
    }

    private async Task AcceptConnectionAsync(QuicConnection quic, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _nextConnectionId);
        var conn = new Conn(id, quic, _cts!);

        try
        {
            var stream = await quic.AcceptInboundStreamAsync(token).ConfigureAwait(false);
            conn.Stream = stream;

            // First framed payload is the handshake carrying the client's ProtocolId.
            var handshake = await ReadFrameAsync(conn).ConfigureAwait(false);
            if (handshake is null || !VerifyHandshake(handshake))
            {
                EnqueueDisconnect(conn, DisconnectReason.ProtocolMismatch, "protocol id mismatch");
                await quic.CloseAsync(0).ConfigureAwait(false);
                await quic.DisposeAsync().ConfigureAwait(false);
                return;
            }

            // Echo our protocol id back so the client can validate the server too.
            SendHandshake(conn);

            _conns[id] = conn;
            RefreshRtt(conn);
            _events.Enqueue(Event.MakeConnected(id));
            _ = ReadLoopAsync(conn);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _events.Enqueue(Event.MakeError(id, $"accept failed: {ex.Message}"));
            try
            {
                await quic.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                /* already gone */
            }
        }
    }

    // ── Client ──────────────────────────────────────────────────────────────

    private async Task RunClientAsync(string host, int port, CancellationToken token)
    {
        QuicConnection? quic = null;
        var conn = new Conn(ServerConnectionId, null!, _cts!);

        try
        {
            var endpoint = await ResolveAsync(host, port).ConfigureAwait(false);

            var options = new QuicClientConnectionOptions {
                RemoteEndPoint = endpoint,
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions {
                    ApplicationProtocols = [Alpn],
                    TargetHost = host,
                    RemoteCertificateValidationCallback =
                        (_, _, _, _) => true, // dev: accept any cert
                },
            };

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.1f, _config.ConnectTimeout)));

            quic = await QuicConnection.ConnectAsync(options, connectCts.Token)
                .ConfigureAwait(false);
            conn.Quic = quic;
            IsRunning = true;

            var stream = await quic.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token)
                .ConfigureAwait(false);
            conn.Stream = stream;

            SendHandshake(conn);

            var ack = await ReadFrameAsync(conn).ConfigureAwait(false);
            if (ack is null || !VerifyHandshake(ack))
            {
                EnqueueDisconnect(
                    conn,
                    DisconnectReason.ProtocolMismatch,
                    "server protocol id mismatch"
                );
                await quic.CloseAsync(0).ConfigureAwait(false);
                await quic.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _conns[ServerConnectionId] = conn;
            RefreshRtt(conn);
            _events.Enqueue(Event.MakeConnected(ServerConnectionId));
            _ = ReadLoopAsync(conn);
        }
        catch (OperationCanceledException)
        {
            IsRunning = false;
            _events.Enqueue(
                Event.MakeDisconnected(ServerConnectionId, DisconnectReason.ConnectFailed)
            );
            if (quic is not null) await SafeDisposeAsync(quic).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            _events.Enqueue(
                Event.MakeError(ServerConnectionId, $"QUIC connect failed: {ex.Message}")
            );
            _events.Enqueue(
                Event.MakeDisconnected(ServerConnectionId, DisconnectReason.ConnectFailed)
            );
            if (quic is not null) await SafeDisposeAsync(quic).ConfigureAwait(false);
        }
    }

    // ── Read loop ───────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(Conn conn)
    {
        try
        {
            while (!conn.Token.IsCancellationRequested)
            {
                var payload = await ReadFrameAsync(conn).ConfigureAwait(false);
                if (payload is null)
                {
                    EnqueueDisconnect(conn, DisconnectReason.RemoteClose, null);
                    return;
                }

                conn.TimeSinceReceive = 0;
                conn.Stats.PacketsReceived++;
                conn.Stats.BytesReceived += conn.LastFrameByteLength;
                conn.WindowBytesReceived += conn.LastFrameByteLength;

                if (conn.LastDelivery == KeepAliveDelivery)
                    continue; // internal ping — no app dispatch

                _events.Enqueue(
                    Event.MakeReceive(
                        conn.Id,
                        payload,
                        (DeliveryMethod)conn.LastDelivery,
                        conn.LastChannel
                    )
                );
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            EnqueueDisconnect(conn, DisconnectReason.TransportError, ex.Message);
        }
    }

    // Reads one length-prefixed frame; returns null on clean stream end. Records delivery/channel/byte-length on conn.
    private static async Task<byte[]?> ReadFrameAsync(Conn conn)
    {
        var stream = conn.Stream!;

        var (length, headerBytes) =
            await ReadVarUIntAsync(stream, conn.Token).ConfigureAwait(false);
        if (length < 0) return null; // EOF before any header byte
        if (length > MaxFrameLength)
            throw new InvalidOperationException($"frame too large ({length} bytes)");

        var delivery = await ReadOneAsync(stream, conn.Token).ConfigureAwait(false);
        var channel = await ReadOneAsync(stream, conn.Token).ConfigureAwait(false);
        if (delivery < 0 || channel < 0) return null;

        var payload = new byte[(int)length];
        if (!await ReadExactAsync(stream, payload, conn.Token).ConfigureAwait(false)) return null;

        conn.LastDelivery = (byte)delivery;
        conn.LastChannel = channel;
        conn.LastFrameByteLength = headerBytes + 2 + (int)length;
        return payload;
    }

    // ── Handshake (ProtocolId) ──────────────────────────────────────────────

    private void SendHandshake(Conn conn)
    {
        Span<byte> id = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(id, _config.ProtocolId);
        SendFrame(
            conn,
            id,
            HandshakeDelivery,
            0
        );
    }

    private bool VerifyHandshake(byte[] payload)
    {
        if (payload.Length < 4) return false;
        var theirId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        return theirId == _config.ProtocolId;
    }

    // ── Connection teardown ─────────────────────────────────────────────────

    private void CloseConnection(Conn conn, DisconnectReason reason, bool notifyRemote)
    {
        if (!conn.MarkClosing()) return;

        _conns.TryRemove(conn.Id, out _);
        if (notifyRemote)
            _events.Enqueue(Event.MakeDisconnected(conn.Id, reason));

        _ = TeardownAsync(conn);
    }

    private void EnqueueDisconnect(Conn conn, DisconnectReason reason, string? error)
    {
        if (!conn.MarkClosing()) return;

        _conns.TryRemove(conn.Id, out _);
        if (error is not null)
            _events.Enqueue(Event.MakeError(conn.Id, error));
        _events.Enqueue(Event.MakeDisconnected(conn.Id, reason));

        _ = TeardownAsync(conn);
    }

    private static async Task TeardownAsync(Conn conn)
    {
        try
        {
            if (conn.Stream is not null) await conn.Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            /* stream may already be torn down */
        }

        if (conn.Quic is not null) await SafeDisposeAsync(conn.Quic).ConfigureAwait(false);
    }

    private static async Task SafeDisposeAsync(QuicConnection quic)
    {
        try
        {
            await quic.CloseAsync(0).ConfigureAwait(false);
        }
        catch
        {
            /* may already be closed */
        }

        try
        {
            await quic.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            /* idempotent */
        }
    }

    private static async Task DisposeListenerAsync(QuicListener? listener)
    {
        if (listener is null) return;
        try
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            /* shutting down */
        }
    }

    private void RefreshRtt(Conn conn)
    {
        // QuicConnection does not expose a public RTT, so seed with the configured estimate.
        conn.Stats.RoundTripTime = _config.InitialRtt;
    }

    // ── Dev certificate ─────────────────────────────────────────────────────

    private static X509Certificate2 CreateDevCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=zigote", ecdsa, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
    }

    private static async Task<IPEndPoint> ResolveAsync(string host, int port)
    {
        if (IPAddress.TryParse(host, out var ip))
            return new IPEndPoint(ip, port);

        var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new InvalidOperationException($"could not resolve host '{host}'");
        return new IPEndPoint(addresses[0], port);
    }

    // ── Stream read helpers ─────────────────────────────────────────────────

    private static async Task<int> ReadOneAsync(QuicStream stream, CancellationToken token)
    {
        var one = new byte[1];
        var read = await stream.ReadAsync(one, token).ConfigureAwait(false);
        return read == 0 ? -1 : one[0];
    }

    private static async Task<bool> ReadExactAsync(QuicStream stream, byte[] buffer,
        CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    // Returns (value, headerByteCount); value == -1 signals a clean EOF before any byte was read.
    private static async Task<(long Value, int HeaderBytes)> ReadVarUIntAsync(QuicStream stream,
        CancellationToken token)
    {
        ulong result = 0;
        var shift = 0;
        var headerBytes = 0;

        while (shift < 35)
        {
            var b = await ReadOneAsync(stream, token).ConfigureAwait(false);
            if (b < 0)
                return headerBytes == 0
                    ? (-1, 0)
                    : throw new EndOfStreamException("truncated length prefix");

            headerBytes++;
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }

        return ((long)result, headerBytes);
    }

    // ── Varint framing helpers ──────────────────────────────────────────────

    private static int VarUIntByteLength(uint value)
    {
        var count = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            count++;
        }

        return count;
    }

    private static int WriteVarUInt(byte[] buffer, uint value)
    {
        var offset = 0;
        while (value >= 0x80)
        {
            buffer[offset++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        buffer[offset++] = (byte)value;
        return offset;
    }

    // ── Per-connection state ────────────────────────────────────────────────

    private sealed class Conn(int id, QuicConnection quic, CancellationTokenSource cts)
    {
        private int _closing;
        public int LastChannel;

        // Scratch from the last decoded frame (read loop is single-consumer, so this is safe).
        public byte LastDelivery;
        public int LastFrameByteLength;

        public float TimeSinceReceive;
        public float TimeSinceSend;
        public long WindowBytesReceived;
        public long WindowBytesSent;
        public float WindowTime;

        public int Id { get; } = id;
        public QuicConnection? Quic { get; set; } = quic;
        public QuicStream? Stream { get; set; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public NetworkStats Stats { get; } = new();
        public CancellationToken Token { get; } = cts.Token;

        public bool Closing => Volatile.Read(ref _closing) != 0;

        public bool MarkClosing()
        {
            return Interlocked.Exchange(ref _closing, 1) == 0;
        }
    }

    // ── Cross-thread event bridge ───────────────────────────────────────────

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
        public DeliveryMethod Delivery { get; private init; }
        public int Channel { get; private init; }
        public string? Error { get; private init; }

        public static Event MakeConnected(int id)
        {
            return new Event {
                Kind = EventKind.Connected,
                ConnectionId = id,
            };
        }

        public static Event MakeDisconnected(int id, DisconnectReason reason)
        {
            return new Event {
                Kind = EventKind.Disconnected,
                ConnectionId = id,
                Reason = reason,
            };
        }

        public static Event MakeReceive(int id, byte[] payload, DeliveryMethod delivery,
            int channel)
        {
            return new Event {
                Kind = EventKind.Receive,
                ConnectionId = id,
                Payload = payload,
                Delivery = delivery,
                Channel = channel,
            };
        }

        public static Event MakeError(int id, string error)
        {
            return new Event {
                Kind = EventKind.Error,
                ConnectionId = id,
                Error = error,
            };
        }
    }
}