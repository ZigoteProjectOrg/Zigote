namespace Zigote.Network;

/// <summary>
///     A decorator over any <see cref="ITransport" /> that injects artificial
///     <see cref="NetworkConditions" />
///     (latency, jitter, loss, duplication, reorder) so games can be developed and tested against
///     bad-network
///     conditions over loopback/localhost. It interposes only on the receive path — inner transport
///     callbacks
///     land here, are copied, scheduled after a simulated one-way delay, and released to the app
///     listener from
///     <see cref="Update" />. Impairing received packets is symmetric: when both endpoints wrap their
///     transport,
///     every hop is impaired exactly once. Sends pass straight through to the inner transport
///     unchanged.
///     <para>
///         Loss is applied only to UNRELIABLE deliveries — dropping a reliable payload would silently
///         break
///         the inner transport's delivery guarantee (it has already committed to delivering it).
///         Reliable payloads
///         are still delayed, jittered, reordered and duplicated.
///     </para>
/// </summary>
public sealed class SimulatedTransport : ITransport, ITransportListener
{
    private readonly ITransport _inner;
    private readonly List<Pending> _pending = [];
    private readonly Random _rng;

    private double _now;

    public SimulatedTransport(ITransport inner, NetworkConditions conditions)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        _rng = new Random(
            conditions.Seed == 0
                ? 0x5A47_4E54
                : conditions.Seed
        ); // "ZGNT" fixed default for reproducibility
        _inner.Listener = this;
    }

    /// <summary>Live-tunable conditions; tests can swap these between updates.</summary>
    public NetworkConditions Conditions { get; set; }

    public TransportRole Role => _inner.Role;
    public bool IsRunning => _inner.IsRunning;

    public ITransportListener? Listener { get; set; }

    public void StartServer(int port) => _inner.StartServer(port);

    public void StartClient(string host, int port) => _inner.StartClient(host: host, port: port);

    public void Send(int connectionId, ReadOnlySpan<byte> payload, DeliveryMethod delivery,
        int channel = 0)
    {
        _inner.Send(
            connectionId: connectionId,
            payload: payload,
            delivery: delivery,
            channel: channel
        );
        // impairment lands on the receive side, not here
    }

    public void Disconnect(int connectionId) => _inner.Disconnect(connectionId);

    public void Stop() => _inner.Stop();

    public NetworkStats? GetStats(int connectionId) => _inner.GetStats(connectionId);

    public void Update(float deltaTime)
    {
        _inner.Update(deltaTime); // drains inner I/O → its callbacks queue Pending deliveries here
        _now += deltaTime;
        ReleaseDue();
    }

    public void Dispose()
    {
        _pending.Clear();
        _inner.Dispose();
    }

    // ---- ITransportListener: inner transport reports here; we schedule to the app listener. ----

    void ITransportListener.OnConnected(int connectionId) => Listener?.OnConnected(connectionId);

    void ITransportListener.OnDisconnected(int connectionId, DisconnectReason reason) =>
        Listener?.OnDisconnected(connectionId: connectionId, reason: reason);

    void ITransportListener.OnError(int connectionId, string error) =>
        Listener?.OnError(connectionId: connectionId, error: error);

    void ITransportListener.OnReceive(int connectionId, ReadOnlySpan<byte> payload,
        DeliveryMethod delivery,
        int channel)
    {
        var c = Conditions;

        if (!delivery.IsReliable() && c.PacketLoss > 0f && _rng.NextDouble() < c.PacketLoss)
        {
            var dropped = _inner.GetStats(connectionId);
            if (dropped is not null) dropped.PacketsDropped++;
            return;
        }

        byte[] copy = payload.ToArray();
        Enqueue(
            connectionId: connectionId,
            payload: copy,
            delivery: delivery,
            channel: channel,
            delay: OneWayDelay(c)
        );

        if (c.DuplicationChance > 0f && _rng.NextDouble() < c.DuplicationChance)
        {
            Enqueue(
                connectionId: connectionId,
                payload: copy,
                delivery: delivery,
                channel: channel,
                delay: OneWayDelay(c) + (_rng.NextDouble() * 0.002)
            );
        }
    }

    private double OneWayDelay(NetworkConditions c)
    {
        double delay = c.LatencySeconds + (_rng.NextDouble() * c.JitterSeconds);
        if (c.ReorderChance > 0f && _rng.NextDouble() < c.ReorderChance)
            delay += _rng.NextDouble() * Math.Max(val1: c.JitterSeconds, val2: c.LatencySeconds);
        return delay;
    }

    private void Enqueue(int connectionId, byte[] payload, DeliveryMethod delivery, int channel,
        double delay)
    {
        _pending.Add(
            new Pending(
                ReleaseTime: _now + delay,
                ConnectionId: connectionId,
                Payload: payload,
                Delivery: delivery,
                Channel: channel
            )
        );
    }

    private void ReleaseDue()
    {
        if (_pending.Count == 0) return;

        // Release in time order so reorder is driven purely by per-packet jitter, not insertion order.
        _pending.Sort(static (a, b) => a.ReleaseTime.CompareTo(b.ReleaseTime));

        int i = 0;
        for (; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (p.ReleaseTime > _now) break;
            Listener?.OnReceive(
                connectionId: p.ConnectionId,
                payload: p.Payload,
                delivery: p.Delivery,
                channel: p.Channel
            );
        }

        if (i > 0) _pending.RemoveRange(index: 0, count: i);
    }

    private readonly record struct Pending(
        double ReleaseTime,
        int ConnectionId,
        byte[] Payload,
        DeliveryMethod Delivery,
        int Channel);
}
