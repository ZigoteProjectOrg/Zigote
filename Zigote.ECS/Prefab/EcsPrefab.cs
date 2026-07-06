using System.Text.Json.Nodes;
using Zigote.Ecs.Reflection;

namespace Zigote.Ecs.Prefab;

/// <summary>
///     A first-class flecs prefab: a reusable entity template whose components are SHARED with every
///     instance via <c>(IsA, prefab)</c>. Editing a prefab component propagates to all instances that
///     haven't overridden it; an instance overrides by setting the component (it then <c>Owns</c> the
///     value) and reverts by removing it. A handle around the prefab entity — see
///     <see cref="EcsPrefabLibrary" />.
/// </summary>
public sealed class EcsPrefab
{
    private readonly EcsWorld _world;

    internal EcsPrefab(EcsWorld world, Entity root, string name)
    {
        _world = world;
        Root = root;
        Name = name;
    }

    /// <summary>The prefab entity (carries the <c>EcsPrefab</c> tag).</summary>
    public Entity Root { get; }

    public string Name { get; }

    /// <summary>Set a shared component on the prefab (inherited by every non-overriding instance).</summary>
    public EcsPrefab With<T>(in T value) where T : unmanaged
    {
        _world.MakeInheritable<T>(); // share via (IsA, prefab) instead of flecs's default copy-on-instantiate
        _world.Set(Root, value);
        return this;
    }

    /// <summary>Spawn an instance that inherits the prefab's components via <c>(IsA, prefab)</c>.</summary>
    public Entity Instantiate()
    {
        return _world.Instantiate(Root);
    }
}

/// <summary>
///     Names + tracks prefabs and (de)serializes instances compactly as <c>{ prefab, overrides }</c> —
///     only the overridden components are stored, so editing the prefab still reaches saved instances.
///     Pure logic over <see cref="EcsWorld" /> + an <see cref="EcsComponentRegistry" />;
///     headless-testable.
/// </summary>
public sealed class EcsPrefabLibrary
{
    private readonly Dictionary<string, EcsPrefab> _byName = new();
    private readonly EcsComponentRegistry _registry;
    private readonly EcsWorld _world;

    public EcsPrefabLibrary(EcsWorld world, EcsComponentRegistry registry)
    {
        _world = world;
        _registry = registry;
    }

    public IReadOnlyCollection<EcsPrefab> Prefabs => _byName.Values;

    /// <summary>Define (or fetch) a named prefab.</summary>
    public EcsPrefab Define(string name)
    {
        if (_byName.TryGetValue(name, out var existing)) return existing;
        var prefab = new EcsPrefab(_world, _world.NewPrefab(name), name);
        return _byName[name] = prefab;
    }

    public EcsPrefab? Get(string name)
    {
        return _byName.GetValueOrDefault(name);
    }

    public Entity Instantiate(string name)
    {
        return _byName.TryGetValue(name, out var p) ? p.Instantiate() : Entity.Null;
    }

    /// <summary>
    ///     Whether the instance overrides <paramref name="type" /> (owns its own copy) vs inherits
    ///     it.
    /// </summary>
    public bool IsOverridden(Entity instance, Type type)
    {
        return _world.Owns(instance, type);
    }

    /// <summary>Drop an override so the field inherits from the prefab again.</summary>
    public bool Revert(Entity instance, Type type)
    {
        return _world.Remove(instance, type);
    }

    // ── Serialization (instance = prefab name + overrides only) ──────────────────

    public JsonObject SerializeInstance(Entity instance, string prefabName)
    {
        var overrides = new JsonArray();
        foreach (var ct in _registry.Types)
            if (_world.Owns(instance, ct.Type) && _world.GetBoxed(instance, ct.Type) is { } boxed)
                overrides.Add(EcsEntitySerializer.ComponentNode(ct, boxed));

        return new JsonObject {
            ["prefab"] = prefabName,
            ["overrides"] = overrides,
        };
    }

    /// <summary>
    ///     Instantiate the named prefab and re-apply its stored overrides (null if the prefab is
    ///     unknown).
    /// </summary>
    public Entity DeserializeInstance(JsonObject data)
    {
        if ((string?)data["prefab"] is not { } prefabName ||
            !_byName.TryGetValue(prefabName, out var prefab))
            return Entity.Null;

        var instance = prefab.Instantiate();
        if (data["overrides"] is JsonArray overrides)
            foreach (var node in overrides)
            {
                if (node is not JsonObject co || (string?)co["type"] is not { } typeName) continue;
                if (_registry.ByName(typeName) is not { } ct) continue;

                // Seed from the inherited value so an override of a subset of fields keeps the rest.
                var boxed = _world.GetBoxed(instance, ct.Type) ??
                            Activator.CreateInstance(ct.Type)!;
                EcsEntitySerializer.ApplyFields(ct, co, boxed);
                _world.SetBoxed(instance, ct.Type, boxed); // owns it now → an override
            }

        return instance;
    }
}