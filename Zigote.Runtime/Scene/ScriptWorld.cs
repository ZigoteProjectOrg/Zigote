using Zigote.Scripting;
using Zigote.Scripting.Metadata;
using Zigote.Scripting.Serialization;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Manages script component instances for one play session.
///     Bridges editor <see cref="SceneNode" /> objects with <see cref="Component" /> instances:
///     syncs transforms in, runs lifecycle, syncs transforms back out.
/// </summary>
public sealed class ScriptWorld : IDisposable
{
    // keyed by SceneNode.Id
    private readonly Dictionary<int, List<Component>> _instances = new();

    // Flat update list carrying the node with its components, in attach order. The fixed tick
    // iterates THIS instead of walking the whole scene tree — a 10k-node level with five scripted
    // nodes must not pay 10k dictionary probes per 120 Hz tick (same index-over-tree rationale as
    // GameSession._bodyIds). Appends mid-tick (World.Spawn) are safe under index iteration and run
    // in the same tick, matching the old walk; removals are deferred to end of tick and rare.
    private readonly List<(SceneNode Node, List<Component> Comps)> _update = [];

    private readonly ScriptRegistry _registry;
    private bool _disposed;

    public ScriptWorld(ScriptRegistry registry) => _registry = registry;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }

    // ── Setup / teardown ──────────────────────────────────────────────────────

    /// <summary>Walk the tree, create component instances, and call OnCreate/OnEnable.</summary>
    public void Attach(SceneNode root) => AttachNode(root);

    /// <summary>
    ///     Attach a subtree spawned mid-play (World.Spawn): create its script instances and run
    ///     OnCreate/OnEnable, exactly like the play-start walk.
    /// </summary>
    public void AttachSubtree(SceneNode node) => AttachNode(node);

    private void AttachNode(SceneNode node)
    {
        if (!string.IsNullOrEmpty(node.ScriptClass))
        {
            var instance = _registry.CreateInstance(node.ScriptClass);
            if (instance != null)
            {
                instance.EntityId = (uint)node.Id;
                SyncToComponent(node: node, comp: instance);

                // Restore serialized field values if present
                if (node.ScriptExports.Count > 0)
                {
                    var meta = _registry.Find(node.ScriptClass);
                    if (meta != null)
                    {
                        ScriptSerializer.Deserialize(
                            instance: instance,
                            meta: meta,
                            stored: node.ScriptExports
                        );
                    }
                }

                Register(node: node, instance: instance);

                instance.CallCreate();
                if (instance.Enabled) instance.CallEnable();
            }
            else
            {
                Console.Error.WriteLine(
                    $"[ScriptWorld] Unknown script class '{node.ScriptClass}' on node '{node.Name}'"
                );
            }
        }

        for (int i = 0; i < node.Children.Count; i++) AttachNode(node.Children[i]);
    }

    /// <summary>
    ///     Create and attach a component of <paramref name="type" /> to a live node (World.AddComponent).
    ///     Resolves through the registry so exported/AOT builds work; returns null for unknown types.
    /// </summary>
    public Component? AddComponent(SceneNode node, Type type)
    {
        if (type.FullName is not { } fullName) return null;
        var instance = _registry.CreateInstance(fullName);
        if (instance == null) return null;

        instance.EntityId = (uint)node.Id;
        SyncToComponent(node: node, comp: instance);
        Register(node: node, instance: instance);

        instance.CallCreate();
        if (instance.Enabled) instance.CallEnable();
        return instance;
    }

    /// <summary>
    ///     Detach a subtree destroyed mid-play (World.Destroy): run OnDisable/OnDestroy on its
    ///     components and drop them, leaving the rest of the world running.
    /// </summary>
    public void DetachSubtree(SceneNode node)
    {
        if (_instances.Remove(key: node.Id, value: out var list))
        {
            foreach (var comp in list)
            {
                if (comp.Enabled) comp.CallDisable();
                comp.CallDestroy();
            }

            // Destroys are deferred to end of tick and rare — a linear scan here is fine.
            for (int i = _update.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(objA: _update[i].Comps, objB: list))
                {
                    _update.RemoveAt(i);
                    break;
                }
            }
        }

        for (int i = 0; i < node.Children.Count; i++) DetachSubtree(node.Children[i]);
    }

    /// <summary>Call OnDisable/OnDestroy on all instances. Called when play stops.</summary>
    public void Detach()
    {
        foreach (var list in _instances.Values)
        foreach (var comp in list)
        {
            if (comp.Enabled) comp.CallDisable();
            comp.CallDestroy();
        }

        _instances.Clear();
        _update.Clear();
    }

    private void Register(SceneNode node, Component instance)
    {
        if (!_instances.TryGetValue(key: node.Id, value: out var list))
        {
            _instances[node.Id] = list = [];
            _update.Add((node, list));
        }

        list.Add(instance);
    }

    // ── Per-frame update ──────────────────────────────────────────────────────

    public void Update(SceneNode root, float dt)
    {
        _ = root; // kept for API stability; the walk was replaced by the _update index
        Time._deltaTime = dt;
        Time._elapsed += dt;

        // Index-based iteration: a script may World.Spawn or World.AddComponent mid-tick, which
        // appends here and runs in this same tick (as the old tree walk did). Removals never
        // happen here — World.Destroy/SetParent are deferred to the end of the tick.
        for (int n = 0; n < _update.Count; n++)
        {
            (var node, var list) = _update[n];
            for (int i = 0; i < list.Count; i++)
            {
                var comp = list[i];
                if (!comp.Enabled) continue;
                SyncToComponent(node: node, comp: comp);
                comp.CallUpdate(dt);
                SyncFromComponent(node: node, comp: comp);
            }
        }
    }

    // ── Component list query ──────────────────────────────────────────────────

    public IReadOnlyList<Component> GetComponents(int nodeId)
    {
        return _instances.TryGetValue(key: nodeId, value: out var list)
            ? list
            : Array.Empty<Component>();
    }

    /// <summary>
    ///     Push a changed exported field's JSON onto every live component on a node — play-mode live
    ///     tuning, so an inspector edit takes effect on the running script immediately. No-op if the
    ///     node has no live components (not scripted, or play hasn't attached it).
    /// </summary>
    public void ApplyExportedField(int nodeId, ExportedField field, string json)
    {
        if (!_instances.TryGetValue(key: nodeId, value: out var list)) return;
        foreach (var comp in list)
            ScriptSerializer.DeserializeField(instance: comp, field: field, json: json);
    }

    // ── Sync helpers ──────────────────────────────────────────────────────────

    private static void SyncToComponent(SceneNode node, Component comp)
    {
        comp.Position = node.Position;
        comp.Rotation = node.Rotation;
        comp.Scale = node.Scale;
    }

    private static void SyncFromComponent(SceneNode node, Component comp)
    {
        node.Position = comp.Position;
        node.Rotation = comp.Rotation;
        node.Scale = comp.Scale;
    }
}
