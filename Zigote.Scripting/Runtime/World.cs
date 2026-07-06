using Zigote.Core.Math3D;
using Zigote.Ecs;

namespace Zigote.Scripting;

/// <summary>
///     A lightweight, copyable handle to a live play-mode entity (a scene node the runtime spawned or
///     that was authored into the scene). The id is the node's session id; 0 is the invalid sentinel,
///     so <c>default(EntityHandle)</c> == <see cref="None" />.
/// </summary>
public readonly struct EntityHandle(uint id) : IEquatable<EntityHandle>
{
    public static EntityHandle None => new(0);

    public uint Id { get; } = id;
    public bool IsValid => Id != 0;

    public bool Equals(EntityHandle other)
    {
        return Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        return obj is EntityHandle h && Equals(h);
    }

    public override int GetHashCode()
    {
        return (int)Id;
    }

    public static bool operator ==(EntityHandle a, EntityHandle b)
    {
        return a.Id == b.Id;
    }

    public static bool operator !=(EntityHandle a, EntityHandle b)
    {
        return a.Id != b.Id;
    }
}

/// <summary>
///     The contract the host (editor play session / game runtime) implements to back the generic
///     <see cref="World" /> scripting API with the live scene. A strongly-typed interface (rather than
///     multiplexed delegates) so it stays debuggable and headless tests can inject a fake backend.
/// </summary>
public interface IWorldBackend
{
    /// <summary>
    ///     Instantiate a <c>.prefab</c> asset (project-relative path). The spawn TRS replaces the prefab
    ///     root's authored position/rotation (children keep their relative transforms); scale is kept.
    /// </summary>
    EntityHandle Spawn(string prefabPath, Vec3 position, Quat rotation, EntityHandle parent);

    /// <summary>Create an empty entity (no visual) — a mount point for components and children.</summary>
    EntityHandle SpawnEmpty(string name, Vec3 position, EntityHandle parent);

    /// <summary>
    ///     Destroy an entity and its whole subtree. Deferred: applied at the end of the current fixed
    ///     tick (after all scripts ran), so handles stay valid for the rest of the tick.
    /// </summary>
    void Destroy(EntityHandle entity);

    bool IsAlive(EntityHandle entity);

    Vec3 GetPosition(EntityHandle entity);
    void SetPosition(EntityHandle entity, Vec3 position);
    Quat GetRotation(EntityHandle entity);
    void SetRotation(EntityHandle entity, Quat rotation);
    Vec3 GetScale(EntityHandle entity);
    void SetScale(EntityHandle entity, Vec3 scale);

    /// <summary>Parent-baked world position (Get/SetPosition operate on the node's own transform fields).</summary>
    Vec3 GetWorldPosition(EntityHandle entity);

    bool GetVisible(EntityHandle entity);
    void SetVisible(EntityHandle entity, bool visible);

    string? GetName(EntityHandle entity);
    string? GetTag(EntityHandle entity);
    void SetTag(EntityHandle entity, string? tag);

    EntityHandle GetParent(EntityHandle entity);

    /// <summary>
    ///     Reparent (deferred like <see cref="Destroy" />). <see cref="EntityHandle.None" /> = scene
    ///     root.
    /// </summary>
    void SetParent(EntityHandle child, EntityHandle parent);

    /// <summary>First live entity with this node name, in tree order.</summary>
    EntityHandle Find(string name);

    int FindAllByTag(string tag, List<EntityHandle> results);
    int CountByTag(string tag);

    /// <summary>
    ///     All live entities within <paramref name="radius" /> (world positions), optionally
    ///     tag-filtered.
    /// </summary>
    int OverlapSphere(Vec3 center, float radius, List<EntityHandle> results, string? tag);

    /// <summary>
    ///     Closest live entity within <paramref name="maxRadius" />, optionally tag-filtered;
    ///     <paramref name="ignore" />
    ///     is skipped.
    /// </summary>
    EntityHandle Nearest(Vec3 center, float maxRadius, string? tag, EntityHandle ignore);

    Component? GetComponent(EntityHandle entity, Type type);
    Component? AddComponent(EntityHandle entity, Type type);
    Component? FindComponent(Type type);

    /// <summary>The flecs entity mirroring this scene entity (attach POD components, run ECS queries).</summary>
    Entity EcsEntity(EntityHandle entity);
}

/// <summary>
///     Generic runtime entity access for scripts: spawn/destroy prefab instances, find entities by
///     name/tag/proximity, and reach their components. Engine-generic — it knows nothing about any
///     game.
///     The host assigns <see cref="Backend" /> in play mode (and clears it on stop); outside play
///     every
///     call is a safe no-op. Mirrors <see cref="Physics" />/<see cref="Audio" />. Every play entity is
///     also a flecs entity (<see cref="EcsEntity" />), so the ECS provider composes with this API.
/// </summary>
public static class World
{
    /// <summary>Set by the host (or a test) to route calls to the live scene.</summary>
    public static IWorldBackend? Backend { get; set; }

    public static bool IsAvailable => Backend != null;

    /// <summary>The handle of the entity a component lives on (its own scene node).</summary>
    public static EntityHandle Of(Component component)
    {
        return new EntityHandle(component.EntityId);
    }

    // ── Spawn / destroy ───────────────────────────────────────────────────────

    public static EntityHandle Spawn(string prefabPath)
    {
        return Backend?.Spawn(
            prefabPath,
            Vec3.Zero,
            Quat.Identity,
            EntityHandle.None
        ) ?? EntityHandle.None;
    }

    public static EntityHandle Spawn(string prefabPath, Vec3 position)
    {
        return Backend?.Spawn(
            prefabPath,
            position,
            Quat.Identity,
            EntityHandle.None
        ) ?? EntityHandle.None;
    }

    public static EntityHandle Spawn(string prefabPath, Vec3 position, Quat rotation)
    {
        return Backend?.Spawn(
            prefabPath,
            position,
            rotation,
            EntityHandle.None
        ) ?? EntityHandle.None;
    }

    public static EntityHandle Spawn(string prefabPath, Vec3 position, Quat rotation,
        EntityHandle parent)
    {
        return Backend?.Spawn(
            prefabPath,
            position,
            rotation,
            parent
        ) ?? EntityHandle.None;
    }

    public static EntityHandle SpawnEmpty(string name, Vec3 position = default)
    {
        return Backend?.SpawnEmpty(name, position, EntityHandle.None) ?? EntityHandle.None;
    }

    public static EntityHandle SpawnEmpty(string name, Vec3 position, EntityHandle parent)
    {
        return Backend?.SpawnEmpty(name, position, parent) ?? EntityHandle.None;
    }

    public static void Destroy(EntityHandle entity)
    {
        Backend?.Destroy(entity);
    }

    public static bool IsAlive(EntityHandle entity)
    {
        return Backend?.IsAlive(entity) ?? false;
    }

    // ── Transform / state ─────────────────────────────────────────────────────

    public static Vec3 GetPosition(EntityHandle entity)
    {
        return Backend?.GetPosition(entity) ?? Vec3.Zero;
    }

    public static void SetPosition(EntityHandle entity, Vec3 position)
    {
        Backend?.SetPosition(entity, position);
    }

    public static Quat GetRotation(EntityHandle entity)
    {
        return Backend?.GetRotation(entity) ?? Quat.Identity;
    }

    public static void SetRotation(EntityHandle entity, Quat rotation)
    {
        Backend?.SetRotation(entity, rotation);
    }

    public static Vec3 GetScale(EntityHandle entity)
    {
        return Backend?.GetScale(entity) ?? Vec3.One;
    }

    public static void SetScale(EntityHandle entity, Vec3 scale)
    {
        Backend?.SetScale(entity, scale);
    }

    public static Vec3 GetWorldPosition(EntityHandle entity)
    {
        return Backend?.GetWorldPosition(entity) ?? Vec3.Zero;
    }

    public static bool GetVisible(EntityHandle entity)
    {
        return Backend?.GetVisible(entity) ?? false;
    }

    public static void SetVisible(EntityHandle entity, bool visible)
    {
        Backend?.SetVisible(entity, visible);
    }

    public static string? GetName(EntityHandle entity)
    {
        return Backend?.GetName(entity);
    }

    public static string? GetTag(EntityHandle entity)
    {
        return Backend?.GetTag(entity);
    }

    public static void SetTag(EntityHandle entity, string? tag)
    {
        Backend?.SetTag(entity, tag);
    }

    public static EntityHandle GetParent(EntityHandle entity)
    {
        return Backend?.GetParent(entity) ?? EntityHandle.None;
    }

    public static void SetParent(EntityHandle child, EntityHandle parent)
    {
        Backend?.SetParent(child, parent);
    }

    // ── Find / queries ────────────────────────────────────────────────────────

    public static EntityHandle Find(string name)
    {
        return Backend?.Find(name) ?? EntityHandle.None;
    }

    public static int FindAllByTag(string tag, List<EntityHandle> results)
    {
        if (Backend is { } b) return b.FindAllByTag(tag, results);
        results.Clear();
        return 0;
    }

    public static int CountByTag(string tag)
    {
        return Backend?.CountByTag(tag) ?? 0;
    }

    public static int OverlapSphere(Vec3 center, float radius, List<EntityHandle> results,
        string? tag = null)
    {
        if (Backend is { } b)
            return b.OverlapSphere(
                center,
                radius,
                results,
                tag
            );
        results.Clear();
        return 0;
    }

    public static EntityHandle Nearest(Vec3 center, float maxRadius, string? tag = null)
    {
        return Backend?.Nearest(
            center,
            maxRadius,
            tag,
            EntityHandle.None
        ) ?? EntityHandle.None;
    }

    public static EntityHandle Nearest(Vec3 center, float maxRadius, string? tag,
        EntityHandle ignore)
    {
        return Backend?.Nearest(
            center,
            maxRadius,
            tag,
            ignore
        ) ?? EntityHandle.None;
    }

    // ── Components ────────────────────────────────────────────────────────────

    public static T? GetComponent<T>(EntityHandle entity) where T : Component
    {
        return Backend?.GetComponent(entity, typeof(T)) as T;
    }

    /// <summary>Attach a new script component to a live entity (OnCreate/OnEnable run immediately).</summary>
    public static T? AddComponent<T>(EntityHandle entity) where T : Component
    {
        return Backend?.AddComponent(entity, typeof(T)) as T;
    }

    /// <summary>First component of a type anywhere in the scene, in tree order (scene-singleton lookup).</summary>
    public static T? FindComponent<T>() where T : Component
    {
        return Backend?.FindComponent(typeof(T)) as T;
    }

    /// <summary>The flecs entity mirroring a scene entity — <see cref="Entity.Null" /> outside play.</summary>
    public static Entity EcsEntity(EntityHandle entity)
    {
        return Backend?.EcsEntity(entity) ?? Entity.Null;
    }
}