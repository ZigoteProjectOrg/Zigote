using Zigote.Ecs;
using Zigote.Ecs.Prefab;
using Zigote.Ecs.Scene;

namespace Zigote.Scripting;

/// <summary>
///     Gives game scripts access to the live flecs world during play — the entity-first counterpart to
///     <see cref="Physics" /> / <see cref="Audio" />. A script can spawn/query entities for data-oriented
///     gameplay (10k projectiles in one query instead of 10k Components), read its own node's canonical
///     <c>Transform</c>, or instantiate runtime prefabs.
///     <para>
///         The host assigns <see cref="World" /> / <see cref="Scene" /> / <see cref="Prefabs" /> on play
///         start and clears them on stop; outside play every accessor is null and the helpers are safe no-ops.
///         Mirrors the static-provider pattern of <see cref="Input" /> / <see cref="Physics" />.
///     </para>
/// </summary>
public static class Ecs
{
    /// <summary>The live flecs world (entities, components, queries, systems). Null outside play.</summary>
    public static EcsWorld? World { get; set; }

    /// <summary>Maps editor nodes ↔ entities and holds the canonical Transforms. Null outside play.</summary>
    public static EcsSceneBridge? Scene { get; set; }

    /// <summary>Runtime prefab registry — define + instantiate templates while playing. Null outside play.</summary>
    public static EcsPrefabLibrary? Prefabs { get; set; }

    public static bool IsAvailable => World != null;

    /// <summary>The entity backing an editor node id (<see cref="Entity.Null" /> if unmapped / outside play).</summary>
    public static Entity EntityForNode(int nodeId)
    {
        return Scene?.EntityOf(nodeId) ?? Entity.Null;
    }

    /// <summary>The entity backing the calling component's node (via its <see cref="Component.EntityId" />).</summary>
    public static Entity EntityFor(Component component)
    {
        return EntityForNode((int)component.EntityId);
    }

    /// <summary>Spawn a bare entity (<see cref="Entity.Null" /> outside play).</summary>
    public static Entity CreateEntity()
    {
        return World?.CreateEntity() ?? Entity.Null;
    }

    /// <summary>Instantiate a named runtime prefab (<see cref="Entity.Null" /> if unknown / outside play).</summary>
    public static Entity Instantiate(string prefab)
    {
        return Prefabs?.Instantiate(prefab) ?? Entity.Null;
    }
}