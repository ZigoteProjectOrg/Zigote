namespace Zigote.Network;

/// <summary>
///     Maps message/RPC types to a stable 16-bit wire id and back to a factory. The id is a
///     deterministic
///     hash of the type's full name, so client and server agree without registering in the same order
///     — only
///     the type names must match. Id collisions are detected and thrown at registration time.
/// </summary>
public sealed class MessageRegistry
{
    private readonly Dictionary<ushort, Func<INetMessage>> _factoryById = new();
    private readonly Dictionary<Type, ushort> _idByType = new();
    private readonly Dictionary<ushort, Type> _typeById = new();

    public ushort Register<T>() where T : INetMessage, new()
    {
        var type = typeof(T);
        if (_idByType.TryGetValue(type, out var existing)) return existing;

        var id = StableId(type);
        if (_typeById.TryGetValue(id, out var clash) && clash != type)
            throw new InvalidOperationException(
                $"Network message id collision ({id}) between '{type.FullName}' and '{clash.FullName}'. Rename one."
            );

        _idByType[type] = id;
        _typeById[id] = type;
        _factoryById[id] = static () => new T();
        return id;
    }

    public ushort GetId<T>() where T : INetMessage
    {
        return GetId(typeof(T));
    }

    public ushort GetId(Type type)
    {
        return _idByType.TryGetValue(type, out var id)
            ? id
            : throw new InvalidOperationException(
                $"Message type '{type.FullName}' is not registered."
            );
    }

    /// <summary>Instantiate a registered message by id, or null if the id is unknown.</summary>
    public INetMessage? Create(ushort id)
    {
        return _factoryById.TryGetValue(id, out var factory) ? factory() : null;
    }

    public bool IsRegistered(ushort id)
    {
        return _typeById.ContainsKey(id);
    }

    private static ushort StableId(Type type)
    {
        var name = type.FullName ?? type.Name;
        var hash = 2166136261;
        foreach (var c in name)
        {
            hash ^= c;
            hash *= 16777619;
        }

        var folded = (ushort)((hash & 0xFFFF) ^ (hash >> 16));
        return folded == 0 ? (ushort)1 : folded; // reserve 0 as "none"
    }
}