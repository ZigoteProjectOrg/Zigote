using System.Runtime.InteropServices;

namespace Zigote.Ecs.Scene;

/// <summary>
///     Maps a scene-node tree (<see cref="IEcsSceneNode" />) to flecs entities and back, with the
///     entity
///     <see cref="Transform" /> as the canonical placement during play and the node tree as the render
///     mirror. The "entity-first inside, OOP outside" spine:
///     <list type="bullet">
///         <item>
///             <see cref="BuildFrom" /> bakes the authored node tree into entities + Transform +
///             ChildOf.
///         </item>
///         <item>During play, scripts/physics write entity Transforms (<see cref="SetTransform" />).</item>
///         <item>
///             <see cref="PullTransforms" /> mirrors entity Transforms back onto the nodes for
///             rendering.
///         </item>
///     </list>
///     Generic over <see cref="IEcsSceneNode" /> so it stays editor-free and headless-testable.
/// </summary>
public sealed class EcsSceneBridge : IDisposable
{
    private readonly Dictionary<ulong, int> _entityToNode = new();

    // Last TRS written to (or read back from) each node's entity — PushTransforms skips the FFI
    // write for a node whose transform hasn't moved since, so a static scene costs no Sets per tick.
    private readonly Dictionary<int, Transform> _lastPushed = new();
    private readonly Dictionary<int, Entity> _nodeToEntity = new();
    private readonly bool _ownsWorld;

    public EcsSceneBridge(EcsWorld? world = null)
    {
        World = world ?? new EcsWorld();
        _ownsWorld = world is null;
    }

    /// <summary>The flecs world — exposed so scripts/systems/prefabs can query the live entity set.</summary>
    public EcsWorld World { get; }

    public IReadOnlyDictionary<int, Entity> NodeEntities => _nodeToEntity;

    public void Dispose()
    {
        _nodeToEntity.Clear();
        _entityToNode.Clear();
        _lastPushed.Clear();
        if (_ownsWorld) World.Dispose();
    }

    // ── Build / lifecycle ────────────────────────────────────────────────────────

    /// <summary>Create (or refresh) an entity per node in the subtree, mirroring transforms + hierarchy.</summary>
    public void BuildFrom(IEcsSceneNode root) => BuildNode(node: root, parent: Entity.Null);

    private void BuildNode(IEcsSceneNode node, Entity parent)
    {
        var e = EnsureEntity(node);
        if (!parent.IsNull) World.SetParent(child: e, parent: parent);
        foreach (var child in node.Children) BuildNode(node: child, parent: e);
    }

    /// <summary>Entity for a node, creating + seeding its Transform on first sight (idempotent).</summary>
    public Entity EnsureEntity(IEcsSceneNode node)
    {
        if (_nodeToEntity.TryGetValue(key: node.Id, value: out var existing)) return existing;

        // UNNAMED on purpose. The editor allows duplicate node names, but flecs entity names are unique
        // per scope (and treat '.' as a path) — CreateEntity(name) would lookup-or-create, collapsing two
        // same-named nodes onto ONE entity and then making it a child of itself via SetParent → flecs
        // aborts (SIGABRT). The bridge keys on node.Id in its own map and never needs the flecs name.
        var e = World.CreateEntity();
        World.Set(e: e, c: ToTransform(node));
        _nodeToEntity[node.Id] = e;
        _entityToNode[e.Raw] = node.Id;
        return e;
    }

    /// <summary>Destroy the entities for a node and its whole subtree (e.g. node deleted in the editor).</summary>
    public void RemoveNode(IEcsSceneNode node)
    {
        foreach (var child in node.Children) RemoveNode(child);
        if (_nodeToEntity.Remove(key: node.Id, value: out var e))
        {
            _entityToNode.Remove(e.Raw);
            _lastPushed.Remove(node.Id);
            World.DestroyEntity(e);
        }
    }

    // ── Lookups ──────────────────────────────────────────────────────────────────

    public Entity EntityOf(int nodeId) => _nodeToEntity.GetValueOrDefault(nodeId);

    public bool TryNodeId(Entity e, out int nodeId) =>
        _entityToNode.TryGetValue(key: e.Raw, value: out nodeId);

    // ── Transform hand-off ───────────────────────────────────────────────────────

    public bool TryGetTransform(int nodeId, out Transform transform)
    {
        if (_nodeToEntity.TryGetValue(key: nodeId, value: out var e))
            return World.TryGet(e: e, value: out transform);
        transform = Transform.Identity;
        return false;
    }

    public void SetTransform(int nodeId, in Transform transform)
    {
        if (_nodeToEntity.TryGetValue(key: nodeId, value: out var e)) World.Set(e: e, c: transform);
    }

    /// <summary>
    ///     Scene → entities: copy authored node TRS into the canonical entity Transforms.
    ///     Change-gated per node — a transform equal to the last one pushed skips the FFI write.
    /// </summary>
    public void PushTransforms(IEcsSceneNode root)
    {
        if (_nodeToEntity.TryGetValue(key: root.Id, value: out var e))
        {
            var t = ToTransform(root);
            // One hashed lookup for gate-read + gate-write instead of TryGetValue + indexer —
            // this runs for every node every render frame.
            ref var last = ref CollectionsMarshal.GetValueRefOrAddDefault(
                dictionary: _lastPushed,
                key: root.Id,
                exists: out bool existed
            );
            if (!existed
                || last.Position != t.Position
                || last.Rotation != t.Rotation
                || last.Scale != t.Scale)
            {
                World.Set(e: e, c: t);
                last = t;
            }
        }

        var children = root.Children;
        for (int i = 0; i < children.Count; i++) PushTransforms(children[i]);
    }

    /// <summary>Entities → scene: mirror canonical entity Transforms back onto the nodes for rendering.</summary>
    public void PullTransforms(IEcsSceneNode root)
    {
        if (_nodeToEntity.TryGetValue(key: root.Id, value: out var e) &&
            World.TryGet<Transform>(e: e, value: out var t))
        {
            root.Position = t.Position;
            root.Rotation = t.Rotation;
            root.Scale = t.Scale;
            // Node now mirrors the entity — keeps the push gate accurate.
            CollectionsMarshal.GetValueRefOrAddDefault(
                dictionary: _lastPushed,
                key: root.Id,
                exists: out _
            ) = t;
        }

        var children = root.Children;
        for (int i = 0; i < children.Count; i++) PullTransforms(children[i]);
    }

    private static Transform ToTransform(IEcsSceneNode n)
    {
        return new Transform {
            Position = n.Position,
            Rotation = n.Rotation,
            Scale = n.Scale,
        };
    }
}
