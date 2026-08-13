using Zigote.Core.Math3D;

namespace Zigote.Network;

/// <summary>
///     A server-authoritative replicated object. Subclass it, declare <see cref="NetVar{T}" /> fields and
///     register them in the constructor with <see cref="Register{TVar}" />; the server streams their changes
///     to clients, which instantiate a matching subclass (by <see cref="PrefabId" />) and apply the state.
///     Override the lifecycle hooks for spawn/despawn glue and <see cref="Interpolate" /> for smoothing.
/// </summary>
public abstract class NetObject
{
    private readonly List<INetVar> _vars = [];

    /// <summary>Network-wide identity, assigned by the server on spawn.</summary>
    public NetId Id { get; internal set; }

    /// <summary>Type tag the client uses to instantiate the right subclass. Set when spawning.</summary>
    public ushort PrefabId { get; internal set; }

    /// <summary>Connection id of the owning client, or -1 for a server-owned object.</summary>
    public int OwnerConnectionId { get; internal set; } = -1;

    /// <summary>True on the authoritative server instance.</summary>
    public bool IsServer { get; internal set; }

    /// <summary>True on the client instance that owns this object (its local player drives it via prediction).</summary>
    public bool IsOwner { get; internal set; }

    /// <summary>The server holds authority; the owning client may still predict locally (see the prediction helpers).</summary>
    public bool HasAuthority => IsServer;

    /// <summary>The registered replicated fields, in declaration order (the order both ends must agree on).</summary>
    protected internal IReadOnlyList<INetVar> Vars => _vars;

    /// <summary>World position used for distance-based interest, if relevant. Override on spatial objects.</summary>
    public virtual Vec3 RelevancePosition => Vec3.Zero;

    protected TVar Register<TVar>(TVar var) where TVar : INetVar
    {
        _vars.Add(var);
        return var;
    }

    internal bool AnyDirty()
    {
        for (var i = 0; i < _vars.Count; i++)
            if (_vars[i].IsDirty)
                return true;
        return false;
    }

    internal void SerializeFull(NetWriter writer)
    {
        for (var i = 0; i < _vars.Count; i++) _vars[i].Serialize(writer);
    }

    internal void ApplyFull(NetReader reader)
    {
        for (var i = 0; i < _vars.Count; i++) _vars[i].Deserialize(reader);
    }

    internal void SerializeDelta(NetWriter writer)
    {
        for (var i = 0; i < _vars.Count; i++) writer.WriteBool(_vars[i].IsDirty);
        for (var i = 0; i < _vars.Count; i++)
            if (_vars[i].IsDirty)
                _vars[i].Serialize(writer);
    }

    internal void ApplyDelta(NetReader reader)
    {
        var dirty = _vars.Count <= 64 ? stackalloc bool[_vars.Count] : new bool[_vars.Count];
        for (var i = 0; i < _vars.Count; i++) dirty[i] = reader.ReadBool();
        for (var i = 0; i < _vars.Count; i++)
            if (dirty[i])
                _vars[i].Deserialize(reader);
    }

    internal void TickVars()
    {
        for (var i = 0; i < _vars.Count; i++) _vars[i].Tick();
    }

    /// <summary>Server: called once on the frame the object is spawned. Client: called after instantiation + first full state.</summary>
    public virtual void OnNetworkSpawn()
    {
    }

    /// <summary>Called immediately before the object is removed on either side.</summary>
    public virtual void OnNetworkDespawn()
    {
    }

    /// <summary>Client hook to advance any interpolation toward <paramref name="renderTime" /> (server render timeline).</summary>
    public virtual void Interpolate(double renderTime)
    {
    }

    /// <summary>Client hook fired after a state packet is applied, carrying the server time it represents.</summary>
    protected internal virtual void OnStateApplied(double serverTime)
    {
    }
}
