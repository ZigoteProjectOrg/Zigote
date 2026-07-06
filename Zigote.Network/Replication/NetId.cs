namespace Zigote.Network;

/// <summary>
///     Stable, network-wide identity of a replicated object. Assigned by the server when an object is
///     spawned and echoed to clients so both ends agree on which object a snapshot/RPC targets. A
///     lightweight value type — copy freely.
/// </summary>
public readonly struct NetId(uint value) : IEquatable<NetId>
{
    public const uint InvalidValue = 0;
    public static readonly NetId None = new(InvalidValue);

    public uint Value { get; } = value;
    public bool IsValid => Value != InvalidValue;

    public bool Equals(NetId other)
    {
        return Value == other.Value;
    }

    public override bool Equals(object? obj)
    {
        return obj is NetId o && Equals(o);
    }

    public override int GetHashCode()
    {
        return (int)Value;
    }

    public override string ToString()
    {
        return IsValid ? $"NetId({Value})" : "NetId(none)";
    }

    public static bool operator ==(NetId a, NetId b)
    {
        return a.Value == b.Value;
    }

    public static bool operator !=(NetId a, NetId b)
    {
        return a.Value != b.Value;
    }
}