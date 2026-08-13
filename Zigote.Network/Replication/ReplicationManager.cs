namespace Zigote.Network;

/// <summary>
///     Server-authoritative object replication. On the server it assigns ids, diffs each connection's
///     area-of-interest to emit reliable spawn/despawn events, and streams unreliable per-tick field
///     deltas.
///     On the client it instantiates objects by prefab id and applies the incoming state. Both sides
///     raise
///     <see cref="ObjectSpawned" /> / <see cref="ObjectDespawned" />.
///     <para>
///         Wire model (lean, loss-tolerant, no baseline-ack bookkeeping): spawns/despawns are reliable
///         and
///         ordered; state is unreliable-sequenced with each object's block length-prefixed so a client
///         can skip an
///         object it hasn't spawned yet, and each changed field stays dirty for a short redundancy
///         window so a
///         single dropped state packet doesn't lose a change. True delta-against-acked-baseline is a
///         future
///         optimization (see ROADMAP).
///     </para>
/// </summary>
public sealed class ReplicationManager(bool isServer)
{
    private readonly NetWriter _block = new(512);
    private readonly List<NetId> _despawns = [];

    private readonly Dictionary<int, HashSet<NetId>>
        _known = new(); // server: per-connection known ids

    private readonly Dictionary<NetId, NetObject> _objects = new();
    private readonly Dictionary<ushort, Func<NetObject>> _prefabs = new();
    private readonly HashSet<NetId> _relevant = [];
    private readonly List<NetId> _spawns = [];
    private readonly List<NetId> _stateList = [];
    private readonly NetReader _subReader = new();
    private IInterestManagement _interest = AlwaysRelevant.Instance;

    private uint _nextId = 1;
    private byte[] _scratch = new byte[512];

    public bool IsServer => isServer;

    public IInterestManagement Interest
    {
        get => _interest;
        set => _interest = value ?? AlwaysRelevant.Instance;
    }

    public IReadOnlyDictionary<NetId, NetObject> Objects => _objects;

    /// <summary>
    ///     Raised when an object enters this peer's world (server: on spawn; client: on receiving a
    ///     spawn).
    /// </summary>
    public event Action<NetObject>? ObjectSpawned;

    /// <summary>Raised just before an object leaves this peer's world.</summary>
    public event Action<NetObject>? ObjectDespawned;

    /// <summary>
    ///     Client: map a prefab id to a factory that builds the matching <see cref="NetObject" />
    ///     subclass.
    /// </summary>
    public void RegisterPrefab(ushort prefabId, Func<NetObject> factory) =>
        _prefabs[prefabId] = factory;

    // ── Server ────────────────────────────────────────────────────────────────
    public T Spawn<T>(T obj, ushort prefabId, int owner = -1) where T : NetObject
    {
        obj.Id = new NetId(_nextId++);
        obj.PrefabId = prefabId;
        obj.OwnerConnectionId = owner;
        obj.IsServer = true;
        obj.IsOwner = false;
        _objects[obj.Id] = obj;
        obj.OnNetworkSpawn();
        ObjectSpawned?.Invoke(obj);
        return obj;
    }

    public void Despawn(NetObject obj)
    {
        if (!_objects.Remove(obj.Id)) return;
        obj.OnNetworkDespawn();
        ObjectDespawned?.Invoke(obj);
        // Per-connection despawn events fall out of the interest diff in ServerTick.
    }

    public void OnConnectionAdded(NetConnection conn)
    {
        if (isServer) _known[conn.Id] = [];
    }

    public void OnConnectionRemoved(NetConnection conn) => _known.Remove(conn.Id);

    public void ServerTick(IReadOnlyList<NetConnection> connections, double serverTime)
    {
        for (int i = 0; i < connections.Count; i++)
            BuildForConnection(conn: connections[i], serverTime: serverTime);
        foreach (var obj in _objects.Values) obj.TickVars();
    }

    private void BuildForConnection(NetConnection conn, double serverTime)
    {
        if (!_known.TryGetValue(key: conn.Id, value: out var known)) return;

        _relevant.Clear();
        foreach (var obj in _objects.Values)
        {
            if (_interest.IsRelevant(obj: obj, connection: conn))
                _relevant.Add(obj.Id);
        }

        _spawns.Clear();
        _despawns.Clear();
        foreach (var id in _relevant)
        {
            if (!known.Contains(id))
                _spawns.Add(id);
        }

        foreach (var id in known)
        {
            if (!_relevant.Contains(id))
                _despawns.Add(id);
        }

        if (_spawns.Count > 0 || _despawns.Count > 0)
        {
            var w = conn.BeginSend(NetChannel.ReplicationEvents);
            w.WriteVarUInt((uint)(_spawns.Count + _despawns.Count));

            foreach (var id in _spawns)
            {
                var obj = _objects[id];
                w.WriteByte((byte)ReplicationOp.Spawn);
                w.WriteNetId(id);
                w.WriteUInt16(obj.PrefabId);
                w.WriteVarInt(obj.OwnerConnectionId);
                w.WriteBool(obj.OwnerConnectionId == conn.Id);
                _block.Clear();
                obj.SerializeFull(_block);
                WriteBlock(writer: w, block: _block.AsSpan());
                known.Add(id);
            }

            foreach (var id in _despawns)
            {
                w.WriteByte((byte)ReplicationOp.Despawn);
                w.WriteNetId(id);
                known.Remove(id);
            }

            conn.EndSend(DeliveryMethod.ReliableOrdered);
        }

        _stateList.Clear();
        foreach (var id in known)
        {
            if (_relevant.Contains(id) && !_spawns.Contains(id) &&
                _objects.TryGetValue(key: id, value: out var obj) && obj.AnyDirty())
                _stateList.Add(id);
        }

        if (_stateList.Count > 0)
        {
            var w = conn.BeginSend(NetChannel.ReplicationState);
            w.WriteDouble(serverTime);
            w.WriteVarUInt((uint)_stateList.Count);
            foreach (var id in _stateList)
            {
                var obj = _objects[id];
                w.WriteNetId(id);
                _block.Clear();
                obj.SerializeDelta(_block);
                WriteBlock(writer: w, block: _block.AsSpan());
            }

            conn.EndSend(DeliveryMethod.UnreliableSequenced);
        }
    }

    // ── Client ────────────────────────────────────────────────────────────────
    internal void HandleEvents(NetConnection conn, NetReader reader)
    {
        uint count = reader.ReadVarUInt();
        for (int i = 0; i < count && !reader.Overflow; i++)
        {
            var op = (ReplicationOp)reader.ReadByte();
            if (op == ReplicationOp.Spawn)
            {
                var id = reader.ReadNetId();
                ushort prefabId = reader.ReadUInt16();
                int owner = reader.ReadVarInt();
                bool isOwner = reader.ReadBool();
                var block = ReadBlock(reader);

                if (_objects.ContainsKey(id)) continue;
                if (!_prefabs.TryGetValue(key: prefabId, value: out var factory)) continue;

                var obj = factory();
                obj.Id = id;
                obj.PrefabId = prefabId;
                obj.OwnerConnectionId = owner;
                obj.IsServer = false;
                obj.IsOwner = isOwner;
                _subReader.SetSource(block);
                obj.ApplyFull(_subReader);
                _objects[id] = obj;
                obj.OnNetworkSpawn();
                ObjectSpawned?.Invoke(obj);
            }
            else
            {
                var id = reader.ReadNetId();
                if (_objects.Remove(key: id, value: out var obj))
                {
                    obj.OnNetworkDespawn();
                    ObjectDespawned?.Invoke(obj);
                }
            }
        }
    }

    internal void HandleState(NetConnection conn, NetReader reader)
    {
        double serverTime = reader.ReadDouble();
        uint count = reader.ReadVarUInt();
        for (int i = 0; i < count && !reader.Overflow; i++)
        {
            var id = reader.ReadNetId();
            var block = ReadBlock(reader);
            if (!_objects.TryGetValue(key: id, value: out var obj)) continue;

            _subReader.SetSource(block);
            obj.ApplyDelta(_subReader);
            obj.OnStateApplied(serverTime);
        }
    }

    public void InterpolateClient(double renderTime)
    {
        foreach (var obj in _objects.Values) obj.Interpolate(renderTime);
    }

    public void Clear()
    {
        foreach (var obj in _objects.Values)
        {
            obj.OnNetworkDespawn();
            ObjectDespawned?.Invoke(obj);
        }

        _objects.Clear();
        _known.Clear();
    }

    private static void WriteBlock(NetWriter writer, ReadOnlySpan<byte> block)
    {
        writer.WriteVarUInt((uint)block.Length);
        for (int i = 0; i < block.Length; i++) writer.WriteByte(block[i]);
    }

    private ReadOnlySpan<byte> ReadBlock(NetReader reader)
    {
        int len = (int)reader.ReadVarUInt();
        if (len <= 0) return ReadOnlySpan<byte>.Empty;
        if (_scratch.Length < len)
            _scratch = new byte[Math.Max(val1: len, val2: _scratch.Length * 2)];
        for (int i = 0; i < len; i++) _scratch[i] = reader.ReadByte();
        return _scratch.AsSpan(start: 0, length: len);
    }
}
