namespace Zigote.Physics2D;

/// <summary>
///     A lightweight, copyable handle to a collider owned by a <see cref="CollisionWorld2D" />.
///     Id 0 is the invalid/none handle; ids are never reused, so a handle to a removed collider
///     stays dead forever.
/// </summary>
public readonly struct ColliderHandle(uint id) : IEquatable<ColliderHandle>
{
    public static ColliderHandle None => default;

    public uint Id { get; } = id;
    public bool IsValid => Id != 0;

    public bool Equals(ColliderHandle other)
    {
        return Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        return obj is ColliderHandle h && Equals(h);
    }

    public override int GetHashCode()
    {
        return (int)Id;
    }

    public static bool operator ==(ColliderHandle a, ColliderHandle b)
    {
        return a.Id == b.Id;
    }

    public static bool operator !=(ColliderHandle a, ColliderHandle b)
    {
        return a.Id != b.Id;
    }

    public override string ToString()
    {
        return Id == 0 ? "ColliderHandle.None" : $"ColliderHandle({Id})";
    }
}

/// <summary>
///     Collider shapes are axis-aligned in v1 — no rotation (boxes stay AABBs; circles are
///     invariant).
/// </summary>
public enum ColliderShape2D
{
    Box,
    Circle,
}