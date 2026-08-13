namespace Zigote.Ecs;

/// <summary>
///     A flecs entity handle: index + generation packed into one <c>ulong</c>.
///     The null/invalid entity is <see cref="Raw" /> == 0.
/// </summary>
public readonly struct Entity : IEquatable<Entity>
{
    public readonly ulong Raw;

    public Entity(ulong raw)
    {
        Raw = raw;
    }

    public static readonly Entity Null = new(0);

    public bool IsNull => Raw == 0;

    public bool Equals(Entity other)
    {
        return Raw == other.Raw;
    }

    public override bool Equals(object? obj)
    {
        return obj is Entity e && Equals(e);
    }

    public override int GetHashCode()
    {
        return Raw.GetHashCode();
    }

    public static bool operator ==(Entity a, Entity b)
    {
        return a.Raw == b.Raw;
    }

    public static bool operator !=(Entity a, Entity b)
    {
        return a.Raw != b.Raw;
    }

    public override string ToString()
    {
        return IsNull ? "Entity.Null" : $"Entity({Raw})";
    }

    public static implicit operator ulong(Entity e)
    {
        return e.Raw;
    }
}

/// <summary>flecs observer event kinds (passed to <see cref="EcsWorld.RegisterObserver{T}" />).</summary>
public enum EcsEvent : byte
{
    OnAdd = 0,
    OnSet = 1,
    OnRemove = 2,
}

/// <summary>flecs pipeline phases (passed to <see cref="EcsWorld.RegisterSystem" />).</summary>
public enum EcsPhase : byte
{
    OnStart = 0,
    PreFrame = 1,
    OnLoad = 2,
    PostLoad = 3,
    PreUpdate = 4,
    OnUpdate = 5,
    OnValidate = 6,
    PostUpdate = 7,
    PreStore = 8,
    OnStore = 9,
    PostFrame = 10,
}
