namespace Zigote.Network;

/// <summary>One-byte tag at the head of every datagram this endpoint emits.</summary>
internal enum PacketKind : byte
{
    Unreliable = 0,
    UnreliableSequenced = 1,
    Reliable = 2,
    Ack = 3,
    Fragment = 4,
    Ping = 5,
    Pong = 6,
}

/// <summary>
///     Pure, socket-agnostic reliability layer for one remote peer: ack/retransmit, ordered/unordered
///     delivery, sequencing, fragmentation and keepalive — all driven by an accumulated virtual clock
///     so it
///     is fully deterministic and unit-testable. It never touches a socket: outgoing datagrams go to
///     the
///     <c>sendRaw</c> delegate the owner supplies, and the owner feeds arriving datagrams to
///     <see cref="ReceiveRaw" />.
/// </summary>
public sealed class ReliableEndpoint
{
    private const int ChannelCount = 256;
    private const int ReceivedWindow = 4096; // sliding dedupe window for reliable ids

    // --- receive side ---
    private readonly Queue<ulong> _ackBacklog = new();

    private readonly Queue<Outgoing>
        _backlog = new(); // reliable sends waiting on MaxReliableInFlight

    private readonly NetConfig _config;
    private readonly Dictionary<ulong, FragmentAssembly> _fragments = new();

    private readonly Dictionary<ushort, byte[]>[]
        _orderedBuffers; // per channel: orderedSeq -> payload

    private readonly ushort[] _orderedExpected = new ushort[ChannelCount];
    private readonly ushort[] _orderedSendSeq = new ushort[ChannelCount];

    // --- send side ---
    private readonly Dictionary<ulong, Pending> _pending = new();
    private readonly NetReader _reader = new();
    private readonly Queue<ulong> _receivedOrder = new(); // FIFO eviction for the dedupe window
    private readonly HashSet<ulong> _receivedReliable = [];
    private readonly Action<ReadOnlyMemory<byte>> _sendRaw;
    private readonly int[] _sequencedLastSeen; // per channel last-seen seq, -1 = none
    private readonly ushort[] _sequencedSendSeq = new ushort[ChannelCount];
    private readonly NetWriter _writer = new();
    private double _lastReceiveTime;
    private double _lastSendTime;
    private ulong _nextMsgId = 1;
    private ulong _nextReliableId = 1;

    // --- clock / keepalive / rtt ---
    private double _now;
    private uint _pingNonce = 1;
    private bool _rttSeeded;
    private float _rttvar;
    private float _srtt;
    private long _windowResent;

    // --- loss estimate window ---
    private long _windowSent;
    private double _windowStart;

    public ReliableEndpoint(NetConfig config, Action<ReadOnlyMemory<byte>> sendRaw)
    {
        _config = config;
        _sendRaw = sendRaw;
        _srtt = config.InitialRtt;
        _rttvar = config.InitialRtt * 0.5f;

        _orderedBuffers = new Dictionary<ushort, byte[]>[ChannelCount];
        _sequencedLastSeen = new int[ChannelCount];
        for (var i = 0; i < ChannelCount; i++)
        {
            _orderedBuffers[i] = new Dictionary<ushort, byte[]>();
            _sequencedLastSeen[i] = -1;
        }
    }

    public NetworkStats Stats { get; } = new();

    public float TimeSinceLastReceive => (float)(_now - _lastReceiveTime);

    // ---------------------------------------------------------------- send helpers

    private int MaxReliablePayload =>
        Math.Max(16, _config.Mtu - 24); // leave headroom for kind + ids + channel + length prefix

    public event Action<byte[], DeliveryMethod, byte>? MessageReceived;
    public event Action? KeepAliveDue;

    public void SendMessage(ReadOnlySpan<byte> payload, DeliveryMethod delivery, byte channel)
    {
        if (delivery.IsReliable())
        {
            if (payload.Length > MaxReliablePayload) SendFragmented(payload, delivery, channel);
            else
                EnqueueReliable(
                    payload,
                    delivery,
                    channel,
                    false,
                    0,
                    0,
                    0
                );
            return;
        }

        if (delivery == DeliveryMethod.UnreliableSequenced)
            SendUnreliableSequenced(payload, channel);
        else
            SendUnreliable(payload, channel);
    }

    public void ReceiveRaw(ReadOnlySpan<byte> datagram)
    {
        _lastReceiveTime = _now;
        Stats.PacketsReceived++;
        Stats.BytesReceived += datagram.Length;

        _reader.SetSource(datagram);
        var kind = (PacketKind)_reader.ReadByte();
        if (_reader.Overflow) return;

        switch (kind)
        {
            case PacketKind.Unreliable: ReadUnreliable(); break;
            case PacketKind.UnreliableSequenced: ReadUnreliableSequenced(); break;
            case PacketKind.Reliable: ReadReliable(); break;
            case PacketKind.Ack: ReadAck(); break;
            case PacketKind.Fragment: ReadFragment(); break;
            case PacketKind.Ping: ReadPing(); break;
            case PacketKind.Pong: ReadPong(); break;
        }
    }

    public void Update(float dt)
    {
        _now += dt;

        // Promote backlogged reliable sends while there's room in flight.
        while (_backlog.Count > 0 && _pending.Count < _config.MaxReliableInFlight)
        {
            var o = _backlog.Dequeue();
            TransmitReliable(o);
        }

        // Retransmit any pending reliable past its RTO.
        var rto = ComputeRto();
        foreach (var p in _pending.Values)
        {
            if (_now - p.LastSentTime < rto) continue;
            p.LastSentTime = _now;
            _sendRaw.Invoke(p.Datagram);
            Stats.PacketsSent++;
            Stats.PacketsResent++;
            Stats.BytesSent += p.Datagram.Length;
            _windowSent++;
            _windowResent++;
            _lastSendTime = _now;
        }

        FlushAcks();

        // Keepalive: probe with a ping if nothing has gone out for KeepAliveInterval.
        if (_now - _lastSendTime >= _config.KeepAliveInterval)
        {
            SendPing();
            KeepAliveDue?.Invoke();
        }

        Stats.ReliableInFlight = _pending.Count;
        UpdateLossEstimate();
    }

    private void EnqueueReliable(ReadOnlySpan<byte> payload, DeliveryMethod delivery, byte channel,
        bool isFragment, ulong msgId, uint fragIndex, uint fragCount)
    {
        var reliableId = _nextReliableId++;
        ushort orderedSeq = 0;
        if (delivery.IsOrdered() && !isFragment) orderedSeq = _orderedSendSeq[channel]++;

        _writer.Clear();
        if (isFragment)
        {
            _writer.WriteByte((byte)PacketKind.Fragment);
            _writer.WriteVarULong(reliableId);
            _writer.WriteVarULong(msgId);
            _writer.WriteVarUInt(fragIndex);
            _writer.WriteVarUInt(fragCount);
            _writer.WriteByte(channel);
            _writer.WriteByte((byte)delivery);
            _writer.WriteBytes(payload);
        }
        else
        {
            _writer.WriteByte((byte)PacketKind.Reliable);
            _writer.WriteVarULong(reliableId);
            _writer.WriteByte(channel);
            _writer.WriteByte((byte)delivery);
            if (delivery.IsOrdered()) _writer.WriteVarUInt(orderedSeq);
            _writer.WriteBytes(payload);
        }

        var outgoing = new Outgoing(reliableId, _writer.ToArray());
        if (_pending.Count >= _config.MaxReliableInFlight)
            _backlog.Enqueue(outgoing);
        else
            TransmitReliable(outgoing);
    }

    private void TransmitReliable(Outgoing o)
    {
        _pending[o.ReliableId] = new Pending(o.Datagram, _now);
        _sendRaw.Invoke(o.Datagram);
        Stats.PacketsSent++;
        Stats.BytesSent += o.Datagram.Length;
        _windowSent++;
        _lastSendTime = _now;
    }

    private void SendFragmented(ReadOnlySpan<byte> payload, DeliveryMethod delivery, byte channel)
    {
        var msgId = _nextMsgId++;
        var max = MaxReliablePayload;
        var fragCount = (uint)((payload.Length + max - 1) / max);
        for (uint i = 0; i < fragCount; i++)
        {
            var start = (int)i * max;
            var len = Math.Min(max, payload.Length - start);
            EnqueueReliable(
                payload.Slice(start, len),
                delivery,
                channel,
                true,
                msgId,
                i,
                fragCount
            );
        }
    }

    private void SendUnreliable(ReadOnlySpan<byte> payload, byte channel)
    {
        _writer.Clear();
        _writer.WriteByte((byte)PacketKind.Unreliable);
        _writer.WriteByte(channel);
        _writer.WriteBytes(payload);
        EmitNow(_writer.ToArray());
    }

    private void SendUnreliableSequenced(ReadOnlySpan<byte> payload, byte channel)
    {
        var seq = _sequencedSendSeq[channel]++;
        _writer.Clear();
        _writer.WriteByte((byte)PacketKind.UnreliableSequenced);
        _writer.WriteByte(channel);
        _writer.WriteVarUInt(seq);
        _writer.WriteBytes(payload);
        EmitNow(_writer.ToArray());
    }

    private void SendPing()
    {
        var nonce = _pingNonce++;
        _writer.Clear();
        _writer.WriteByte((byte)PacketKind.Ping);
        _writer.WriteVarUInt(nonce);
        EmitNow(_writer.ToArray());
    }

    private void FlushAcks()
    {
        while (_ackBacklog.Count > 0)
        {
            var id = _ackBacklog.Dequeue();
            _writer.Clear();
            _writer.WriteByte((byte)PacketKind.Ack);
            _writer.WriteVarULong(id);
            EmitNow(_writer.ToArray());
        }
    }

    private void EmitNow(byte[] datagram)
    {
        _sendRaw.Invoke(datagram);
        Stats.PacketsSent++;
        Stats.BytesSent += datagram.Length;
        _windowSent++;
        _lastSendTime = _now;
    }

    // ---------------------------------------------------------------- receive helpers

    private void ReadUnreliable()
    {
        var channel = _reader.ReadByte();
        var payload = _reader.ReadBytes();
        if (_reader.Overflow) return;
        MessageReceived?.Invoke(payload, DeliveryMethod.Unreliable, channel);
    }

    private void ReadUnreliableSequenced()
    {
        var channel = _reader.ReadByte();
        var seq = (ushort)_reader.ReadVarUInt();
        var payload = _reader.ReadBytes();
        if (_reader.Overflow) return;

        var last = _sequencedLastSeen[channel];
        if (last >= 0 && !IsNewer(seq, (ushort)last))
        {
            Stats.PacketsDropped++;
            return;
        }

        _sequencedLastSeen[channel] = seq;
        MessageReceived?.Invoke(payload, DeliveryMethod.UnreliableSequenced, channel);
    }

    private void ReadReliable()
    {
        var reliableId = _reader.ReadVarULong();
        var channel = _reader.ReadByte();
        var delivery = (DeliveryMethod)_reader.ReadByte();
        ushort orderedSeq = 0;
        if (delivery.IsOrdered()) orderedSeq = (ushort)_reader.ReadVarUInt();
        var payload = _reader.ReadBytes();
        if (_reader.Overflow) return;

        _ackBacklog.Enqueue(
            reliableId
        ); // always re-ack, even on a duplicate, so the sender's retransmit clears
        if (!MarkReceived(reliableId)) return; // duplicate

        if (delivery.IsOrdered())
            DeliverOrdered(
                channel,
                orderedSeq,
                payload,
                delivery
            );
        else
            MessageReceived?.Invoke(payload, delivery, channel);
    }

    private void ReadAck()
    {
        var reliableId = _reader.ReadVarULong();
        if (_reader.Overflow) return;
        if (!_pending.Remove(reliableId, out var p)) return;

        var sample = (float)(_now - p.SentTime);
        if (sample > 0) UpdateRtt(sample);
    }

    private void ReadFragment()
    {
        var reliableId = _reader.ReadVarULong();
        var msgId = _reader.ReadVarULong();
        var fragIndex = _reader.ReadVarUInt();
        var fragCount = _reader.ReadVarUInt();
        var channel = _reader.ReadByte();
        var delivery = (DeliveryMethod)_reader.ReadByte();
        var payload = _reader.ReadBytes();
        if (_reader.Overflow || fragCount == 0 || fragIndex >= fragCount) return;

        _ackBacklog.Enqueue(reliableId);
        if (!MarkReceived(reliableId)) return; // duplicate fragment

        if (!_fragments.TryGetValue(msgId, out var asm))
        {
            asm = new FragmentAssembly(fragCount);
            _fragments[msgId] = asm;
        }

        if (asm.Add(fragIndex, payload) && asm.IsComplete)
        {
            _fragments.Remove(msgId);
            var full = asm.Combine();
            // Fragmented messages carry no ordered seq, so a fully reassembled one is delivered immediately
            // (its constituent fragments were reliably acked, so it can't be lost — only its parts can race,
            // which reassembly resolves). Ordering relative to non-fragmented ordered messages is not preserved.
            MessageReceived?.Invoke(full, delivery, channel);
        }
    }

    private void ReadPing()
    {
        var nonce = _reader.ReadVarUInt();
        if (_reader.Overflow) return;
        _writer.Clear();
        _writer.WriteByte((byte)PacketKind.Pong);
        _writer.WriteVarUInt(nonce);
        EmitNow(_writer.ToArray());
    }

    private void ReadPong()
    {
        _ = _reader.ReadVarUInt(); // nonce — presence already refreshed the receive timer
    }

    private void DeliverOrdered(byte channel, ushort seq, byte[] payload, DeliveryMethod delivery)
    {
        if (seq == _orderedExpected[channel])
        {
            MessageReceived?.Invoke(payload, delivery, channel);
            _orderedExpected[channel]++;
            // Release any contiguous buffered successors.
            var buffer = _orderedBuffers[channel];
            while (buffer.Remove(_orderedExpected[channel], out var next))
            {
                MessageReceived?.Invoke(next, delivery, channel);
                _orderedExpected[channel]++;
            }
        }
        else if (IsNewer(seq, (ushort)(_orderedExpected[channel] - 1)))
        {
            _orderedBuffers[channel][seq] = payload; // future packet — buffer until the gap fills
        }
    }

    // ---------------------------------------------------------------- rtt / loss / clock

    private void UpdateRtt(float sample)
    {
        if (!_rttSeeded)
        {
            _srtt = sample;
            _rttvar = sample * 0.5f;
            _rttSeeded = true;
        }
        else
        {
            _rttvar = 0.75f * _rttvar + 0.25f * MathF.Abs(_srtt - sample);
            _srtt = 0.875f * _srtt + 0.125f * sample;
        }

        Stats.RoundTripTime = _srtt;
        Stats.Jitter = _rttvar;
    }

    private float ComputeRto()
    {
        var rto = _srtt * _config.RetransmitRttFactor + 4f * _rttvar;
        return Math.Clamp(rto, _config.MinRetransmitInterval, _config.MaxRetransmitInterval);
    }

    private void UpdateLossEstimate()
    {
        if (_now - _windowStart < 1.0) return;
        Stats.PacketLoss =
            _windowSent > 0 ? Math.Clamp((float)_windowResent / _windowSent, 0f, 1f) : 0f;
        _windowStart = _now;
        _windowSent = 0;
        _windowResent = 0;
    }

    // ---------------------------------------------------------------- dedupe window

    private bool MarkReceived(ulong reliableId)
    {
        if (!_receivedReliable.Add(reliableId)) return false;
        _receivedOrder.Enqueue(reliableId);
        if (_receivedOrder.Count > ReceivedWindow)
            _receivedReliable.Remove(_receivedOrder.Dequeue());
        return true;
    }

    /// <summary>
    ///     16-bit wraparound-safe "is <paramref name="a" /> strictly newer than <paramref name="b" />
    ///     ".
    /// </summary>
    private static bool IsNewer(ushort a, ushort b)
    {
        return (ushort)(a - b) is > 0 and < 0x8000;
    }

    private readonly struct Outgoing(ulong reliableId, byte[] datagram)
    {
        public readonly ulong ReliableId = reliableId;
        public readonly byte[] Datagram = datagram;
    }

    private sealed class Pending(byte[] datagram, double sentTime)
    {
        public readonly byte[] Datagram = datagram;
        public readonly double SentTime = sentTime;
        public double LastSentTime = sentTime;
    }

    private sealed class FragmentAssembly(uint count)
    {
        private readonly byte[]?[] _parts = new byte[count][];
        private int _have;

        public bool IsComplete => _have == _parts.Length;

        public bool Add(uint index, byte[] payload)
        {
            if (index >= _parts.Length || _parts[index] is not null) return false;
            _parts[index] = payload;
            _have++;
            return true;
        }

        public byte[] Combine()
        {
            var total = 0;
            foreach (var p in _parts) total += p!.Length;
            var result = new byte[total];
            var offset = 0;
            foreach (var p in _parts)
            {
                p!.CopyTo(result, offset);
                offset += p.Length;
            }

            return result;
        }
    }
}
