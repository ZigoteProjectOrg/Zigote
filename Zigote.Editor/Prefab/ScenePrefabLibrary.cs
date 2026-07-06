using System.Text.Json.Nodes;
using Zigote.Ecs;
using Zigote.Ecs.Prefab;
using Zigote.Ecs.Reflection;
using Zigote.Ecs.Scene;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Prefab;

/// <summary>
///     The editor prefab engine, built directly on the flecs-native <see cref="EcsPrefabLibrary" />.
///     A prefab's overridable numeric state (<see cref="Transform" />/<see cref="NodeMaterial" />/
///     <see cref="NodeLight" />) is stored as SHARED components on an <see cref="EcsPrefab" />, so
///     instances inherit via <c>(IsA, prefab)</c>, override by owning a component, revert by removing
///     it, propagate when the prefab is edited, and serialize as overrides-only — all for free from
///     flecs. This wrapper is the SceneNode↔prefab glue: it maps node fields to those components and
///     back. Requires a live <see cref="EcsWorld" /> (native flecs); the structural/string identity of
///     a
///     prefab lives in its <see cref="PrefabDocument" /> template, not here.
/// </summary>
public sealed class ScenePrefabLibrary
{
    private readonly EcsPrefabLibrary _lib;
    private readonly EcsComponentRegistry _registry;

    public ScenePrefabLibrary(EcsWorld world)
    {
        World = world;
        _registry = new EcsComponentRegistry();
        // Catalogue the overridable components so SerializeInstance/override-detection can enumerate them.
        _registry.Register<Transform>();
        _registry.Register<NodeMaterial>();
        _registry.Register<NodeLight>();
        _lib = new EcsPrefabLibrary(world, _registry);
    }

    public EcsWorld World { get; }

    /// <summary>The runtime component types a prefab instance can override (for inspector queries).</summary>
    public static IReadOnlyList<Type> OverridableTypes { get; } =
        [typeof(Transform), typeof(NodeMaterial), typeof(NodeLight)];

    /// <summary>
    ///     Define (or update) a prefab named <paramref name="name" /> from a template node. Re-defining
    ///     writes the template's current values onto the shared prefab components, so the change
    ///     PROPAGATES to every instance that has not overridden that component.
    /// </summary>
    public EcsPrefab DefinePrefab(string name, SceneNode template)
    {
        var prefab = _lib.Define(name);
        prefab.With(SceneNodeComponents.ReadTransform(template));
        if (template.Kind == NodeKind.Mesh)
            prefab.With(SceneNodeComponents.ReadMaterial(template));
        if (template.Kind == NodeKind.Light)
            prefab.With(SceneNodeComponents.ReadLight(template));
        return prefab;
    }

    public EcsPrefab? Get(string name)
    {
        return _lib.Get(name);
    }

    /// <summary>Spawn an instance entity that inherits the named prefab's components.</summary>
    public Entity Instantiate(string name)
    {
        return _lib.Instantiate(name);
    }

    /// <summary>
    ///     Copy the instance's currently-resolved component values (inherited from the prefab unless
    ///     overridden) onto a <see cref="SceneNode" /> so the node reflects the prefab. Read-only on the
    ///     entity — uses <c>TryGet</c>, never triggering an accidental override.
    /// </summary>
    public void ApplyToNode(Entity instance, SceneNode node)
    {
        if (World.TryGet<Transform>(instance, out var t))
            SceneNodeComponents.WriteTransform(node, t);
        if (node.Kind == NodeKind.Mesh && World.TryGet<NodeMaterial>(instance, out var m))
            SceneNodeComponents.WriteMaterial(node, m);
        if (node.Kind == NodeKind.Light && World.TryGet<NodeLight>(instance, out var l))
            SceneNodeComponents.WriteLight(node, l);
    }

    // ── Override / revert (delegates to flecs Owns/Remove semantics) ─────────────

    /// <summary>Override the instance's transform with the node's current value (instance now owns it).</summary>
    public void OverrideTransform(Entity instance, SceneNode node)
    {
        World.Set(instance, SceneNodeComponents.ReadTransform(node));
    }

    public void OverrideMaterial(Entity instance, SceneNode node)
    {
        World.Set(instance, SceneNodeComponents.ReadMaterial(node));
    }

    public void OverrideLight(Entity instance, SceneNode node)
    {
        World.Set(instance, SceneNodeComponents.ReadLight(node));
    }

    /// <summary>
    ///     Whether the instance overrides <paramref name="type" /> (owns it) vs inherits from the
    ///     prefab.
    /// </summary>
    public bool IsOverridden(Entity instance, Type type)
    {
        return _lib.IsOverridden(instance, type);
    }

    /// <summary>
    ///     Drop the override of <paramref name="type" /> so the instance inherits from the prefab again,
    ///     then refresh <paramref name="node" /> from the now-inherited values. Returns false if nothing
    ///     was overridden.
    /// </summary>
    public bool Revert(Entity instance, SceneNode node, Type type)
    {
        if (!_lib.Revert(instance, type)) return false;
        ApplyToNode(instance, node);
        return true;
    }

    // ── Serialization (compact: prefab name + owned overrides only) ──────────────

    public JsonObject SerializeInstance(Entity instance, string prefabName)
    {
        return _lib.SerializeInstance(instance, prefabName);
    }

    /// <summary>Instantiate a prefab and re-apply the stored overrides (Entity.Null if prefab unknown).</summary>
    public Entity DeserializeInstance(JsonObject data)
    {
        return _lib.DeserializeInstance(data);
    }
}