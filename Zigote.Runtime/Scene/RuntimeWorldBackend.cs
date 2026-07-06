using Zigote.Core.Math3D;
using Zigote.Ecs;
using Zigote.Ecs.Scene;
using Zigote.Runtime.Prefab;
using Zigote.Scripting;
using Zigote.World;

namespace Zigote.Runtime.Scene;

/// <summary>
///     The per-subsystem integration a spawned/destroyed subtree needs from the play session (physics
///     bodies, audio sources, VFX simulators). Split out so the backend's scene/index/ledger logic is
///     headless-testable with a recording fake — <c>GameSession</c> is the real implementation.
/// </summary>
internal interface IWorldSessionHooks
{
    /// <summary>
    ///     Register session resources for every node in a freshly spawned subtree (before scripts
    ///     attach).
    /// </summary>
    void OnSpawned(SceneNode subtreeRoot);

    /// <summary>
    ///     Release session resources for every node in a subtree about to be destroyed (after scripts
    ///     detached).
    /// </summary>
    void OnDestroying(SceneNode subtreeRoot);
}

/// <summary>
///     Backs the generic <c>World</c> scripting API in play mode: spawn/destroy prefab instances as
///     live <see cref="SceneNode" />s, find entities by name/tag/proximity, and bridge to their script
///     components and flecs entities. Spawns are immediate (OnCreate runs inside the Spawn call, Unity
///     style); destroys and reparents are deferred to the end of the current fixed tick so handles
///     stay
///     valid while scripts run. Keeps a scene-edit ledger so play stop removes everything it spawned
///     and
///     re-attaches authored nodes a script destroyed/reparented — play can never mutate the authored
///     scene.
/// </summary>
internal sealed class RuntimeWorldBackend : IWorldBackend
{
    // Authored nodes a script structurally disturbed (destroyed or reparented): original parent +
    // child index, re-attached on play stop. Keyed by node id; first disturbance wins.
    // Ordered like an undo stack: each record's index was valid in the tree state right before that
    // disturbance, so play-stop replays them in REVERSE to restore the authored structure exactly
    // (e.g. two siblings destroyed in one scene swap each recorded index 0 as the list shifted).
    private readonly List<(SceneNode node, SceneNode parent, int index)> _authoredRestore = [];
    private readonly HashSet<int> _authoredRestoreIds = [];
    private readonly EcsSceneBridge? _ecs;
    private readonly IWorldSessionHooks? _hooks;

    // Live play entities (authored at play start + spawned), keyed by SceneNode.Id.
    private readonly Dictionary<int, SceneNode> _nodes = new();
    private readonly List<uint> _pendingDestroys = [];
    private readonly HashSet<uint> _pendingDestroySet = [];

    // Deferred structural ops (applied by ApplyDeferred after scripts ran, before physics steps).
    private readonly List<(uint child, uint parent)> _pendingReparents = [];

    // .prefab documents cached per session (null = load failed; logged once).
    private readonly Dictionary<string, PrefabDocument?> _prefabs = new(StringComparer.Ordinal);

    // Authored nodes whose Visible a script changed: original value, restored on play stop.
    private readonly Dictionary<int, (SceneNode node, bool visible)> _savedVisible = new();
    private readonly List<int> _scratch = [];
    private readonly ScriptWorld _scripts;
    private readonly SpatialHash _spatial = new();

    // Every node id inside a spawned subtree — classifies destroys (spawned vs authored).
    private readonly HashSet<int> _spawnedIds = [];

    // Spawned subtree roots still alive; removed from the C# tree + native scene on play stop.
    private readonly List<SceneNode> _spawnedRoots = [];

    private readonly TagIndex _tags = new();
    private bool _spatialDirty = true;
    private int _spatialStamp = -1;
    private int _tick;

    public RuntimeWorldBackend(SceneNode root, ScriptWorld scripts, EcsSceneBridge? ecs,
        IWorldSessionHooks? hooks)
    {
        Root = root;
        _scripts = scripts;
        _ecs = ecs;
        _hooks = hooks;
        RegisterSubtree(root, false); // authored entities: findable/taggable from tick 0
    }

    internal SceneNode Root { get; }

    // ── Spawn / destroy ───────────────────────────────────────────────────────

    public EntityHandle Spawn(string prefabPath, Vec3 position, Quat rotation, EntityHandle parent)
    {
        var parentNode = ResolveParent(parent);
        if (parentNode == null) return EntityHandle.None;

        var doc = LoadPrefab(prefabPath);
        if (doc == null) return EntityHandle.None;

        var node = doc.InstantiateNode();
        node.Position = position;
        node.Rotation = rotation;
        return Integrate(node, parentNode);
    }

    public EntityHandle SpawnEmpty(string name, Vec3 position, EntityHandle parent)
    {
        var parentNode = ResolveParent(parent);
        if (parentNode == null) return EntityHandle.None;

        return Integrate(new SceneNode(name) { Position = position }, parentNode);
    }

    public void Destroy(EntityHandle entity)
    {
        if (!entity.IsValid || !_nodes.ContainsKey((int)entity.Id)) return;
        if (entity.Id == (uint)Root.Id) return; // the scene root is not destroyable
        if (_pendingDestroySet.Add(entity.Id)) _pendingDestroys.Add(entity.Id);
    }

    public bool IsAlive(EntityHandle entity)
    {
        return entity.IsValid && _nodes.ContainsKey((int)entity.Id);
    }

    // ── Transform / state ─────────────────────────────────────────────────────

    public Vec3 GetPosition(EntityHandle entity)
    {
        return TryNode(entity.Id, out var n) ? n.Position : Vec3.Zero;
    }

    public void SetPosition(EntityHandle entity, Vec3 position)
    {
        if (TryNode(entity.Id, out var n))
        {
            n.Position = position;
            _spatialDirty = true;
        }
    }

    public Quat GetRotation(EntityHandle entity)
    {
        return TryNode(entity.Id, out var n) ? n.Rotation : Quat.Identity;
    }

    public void SetRotation(EntityHandle entity, Quat rotation)
    {
        if (TryNode(entity.Id, out var n)) n.Rotation = rotation;
    }

    public Vec3 GetScale(EntityHandle entity)
    {
        return TryNode(entity.Id, out var n) ? n.Scale : Vec3.One;
    }

    public void SetScale(EntityHandle entity, Vec3 scale)
    {
        if (TryNode(entity.Id, out var n)) n.Scale = scale;
    }

    public Vec3 GetWorldPosition(EntityHandle entity)
    {
        return TryNode(entity.Id, out var n) ? WorldTransform(n).Position : Vec3.Zero;
    }

    public bool GetVisible(EntityHandle entity)
    {
        return TryNode(entity.Id, out var n) && n.Visible;
    }

    public void SetVisible(EntityHandle entity, bool visible)
    {
        if (!TryNode(entity.Id, out var n)) return;
        if (!_spawnedIds.Contains(n.Id) && !_savedVisible.ContainsKey(n.Id))
            _savedVisible[n.Id] = (n, n.Visible);
        n.Visible = visible;
    }

    public string? GetName(EntityHandle entity)
    {
        return TryNode(entity.Id, out var n) ? n.Name : null;
    }

    public string? GetTag(EntityHandle entity)
    {
        return _tags.TagOf((int)entity.Id);
    }

    public void SetTag(EntityHandle entity, string? tag)
    {
        // Session-local: the index changes, the authored node.Tag never does — play can't edit the scene.
        if (TryNode(entity.Id, out _)) _tags.Set((int)entity.Id, tag);
    }

    public EntityHandle GetParent(EntityHandle entity)
    {
        return TryNode(entity.Id, out var n) && n.Parent is { } p && _nodes.ContainsKey(p.Id)
            ? new EntityHandle((uint)p.Id)
            : EntityHandle.None;
    }

    public void SetParent(EntityHandle child, EntityHandle parent)
    {
        if (!IsAlive(child)) return;
        _pendingReparents.Add((child.Id, parent.IsValid ? parent.Id : 0));
    }

    // ── Find / queries ────────────────────────────────────────────────────────

    public EntityHandle Find(string name)
    {
        var found = FindByName(Root, name);
        return found != null ? new EntityHandle((uint)found.Id) : EntityHandle.None;
    }

    public int FindAllByTag(string tag, List<EntityHandle> results)
    {
        results.Clear();
        _tags.WithTag(tag, _scratch);
        foreach (var id in _scratch) results.Add(new EntityHandle((uint)id));
        return results.Count;
    }

    public int CountByTag(string tag)
    {
        return _tags.Count(tag);
    }

    public int OverlapSphere(Vec3 center, float radius, List<EntityHandle> results, string? tag)
    {
        results.Clear();
        EnsureSpatial();
        _spatial.Query(center, radius, _scratch);
        foreach (var id in _scratch)
        {
            if (tag != null && _tags.TagOf(id) != tag) continue;
            results.Add(new EntityHandle((uint)id));
        }

        return results.Count;
    }

    public EntityHandle Nearest(Vec3 center, float maxRadius, string? tag, EntityHandle ignore)
    {
        EnsureSpatial();
        _spatial.Query(center, maxRadius, _scratch);
        var bestId = 0;
        var bestD2 = float.MaxValue;
        foreach (var id in _scratch)
        {
            if (ignore.IsValid && (uint)id == ignore.Id) continue;
            if (tag != null && _tags.TagOf(id) != tag) continue;
            if (!_spatial.TryGetPosition(id, out var pos)) continue;
            var d2 = (pos - center).LengthSq();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestId = id;
            }
        }

        return bestId != 0 ? new EntityHandle((uint)bestId) : EntityHandle.None;
    }

    // ── Components / ECS ──────────────────────────────────────────────────────

    public Component? GetComponent(EntityHandle entity, Type type)
    {
        var comps = _scripts.GetComponents((int)entity.Id);
        for (var i = 0; i < comps.Count; i++)
            if (type.IsInstanceOfType(comps[i]))
                return comps[i];
        return null;
    }

    public Component? AddComponent(EntityHandle entity, Type type)
    {
        return TryNode(entity.Id, out var n) ? _scripts.AddComponent(n, type) : null;
    }

    public Component? FindComponent(Type type)
    {
        return FindComponentIn(Root, type);
    }

    public Entity EcsEntity(EntityHandle entity)
    {
        return _ecs?.EntityOf((int)entity.Id) ?? Entity.Null;
    }

    // ── Session driving (GameSession) ─────────────────────────────────────────

    internal void BeginTick()
    {
        _tick++;
    }

    /// <summary>
    ///     Apply deferred reparents + destroys. Reentrancy-safe: an OnDestroy may queue more
    ///     destroys.
    /// </summary>
    internal void ApplyDeferred()
    {
        for (var i = 0; i < _pendingReparents.Count; i++)
        {
            var (childId, parentId) = _pendingReparents[i];
            if (!TryNode(childId, out var child)) continue;
            var parent = parentId == 0 ? Root : TryNode(parentId, out var p) ? p : null;
            if (parent != null) ExecuteReparent(child, parent);
        }

        _pendingReparents.Clear();

        for (var i = 0; i < _pendingDestroys.Count; i++) // index-based: OnDestroy may append
            if (TryNode(_pendingDestroys[i], out var node))
                ExecuteDestroy(node);

        _pendingDestroys.Clear();
        _pendingDestroySet.Clear();
    }

    /// <summary>
    ///     Undo every structural scene edit play made: remove spawned subtrees from the tree + native
    ///     scene, re-attach authored nodes scripts destroyed/reparented, restore visibility. Called on
    ///     play stop after scripts were detached (their OnDestroy already ran).
    /// </summary>
    internal void RestoreSceneEdits()
    {
        foreach (var node in _spawnedRoots)
        {
            node.RemoveFromNative();
            node.Parent?.RemoveChild(node);
        }

        _spawnedRoots.Clear();

        for (var i = _authoredRestore.Count - 1; i >= 0; i--) // reverse: undo-stack replay
        {
            var rec = _authoredRestore[i];
            // Native handles are stale for a moved node; recreate the subtree cleanly on next sync.
            rec.node.RemoveFromNative();
            rec.node.Parent?.RemoveChild(rec.node);
            var index = Math.Min(rec.index, rec.parent.Children.Count);
            rec.parent.Children.Insert(index, rec.node);
            rec.node.Parent = rec.parent;
        }

        _authoredRestore.Clear();
        _authoredRestoreIds.Clear();

        foreach (var (_, rec) in _savedVisible) rec.node.Visible = rec.visible;
        _savedVisible.Clear();
    }

    private SceneNode? ResolveParent(EntityHandle parent)
    {
        if (!parent.IsValid) return Root;
        return TryNode(parent.Id, out var node) ? node : null;
    }

    private EntityHandle Integrate(SceneNode node, SceneNode parentNode)
    {
        parentNode.AddChild(node);
        RegisterSubtree(node, true);
        _spawnedRoots.Add(node);
        _spatialDirty = true;

        // Session resources first, scripts last — OnCreate must see its physics body/audio source live,
        // and can already query the entity (it was registered above). Mirrors the play-start order.
        _hooks?.OnSpawned(node);
        _scripts.AttachSubtree(node);
        return new EntityHandle((uint)node.Id);
    }

    /// <summary>
    ///     Graft an externally-built subtree (an additively-loaded scene) through the spawn machinery,
    ///     so it gets the full integration AND the ledger removes it on play stop.
    /// </summary>
    internal EntityHandle IntegrateExternal(SceneNode subtreeRoot, SceneNode parentNode)
    {
        return Integrate(subtreeRoot, parentNode);
    }

    /// <summary>
    ///     Destroy immediately, bypassing the per-tick deferral — only for the scene-swap path, which
    ///     already runs at the deferred-apply point of the fixed tick.
    /// </summary>
    internal void DestroyNow(EntityHandle entity)
    {
        if (!entity.IsValid || entity.Id == (uint)Root.Id) return;
        if (TryNode(entity.Id, out var node)) ExecuteDestroy(node);
    }

    private void ExecuteDestroy(SceneNode node)
    {
        if (ReferenceEquals(node, Root)) return;

        // Spawned subtrees nested under this node get the full treatment first, so their ledger
        // entries clear and their session resources release individually.
        if (_spawnedRoots.Count > 0)
        {
            List<SceneNode>? nested = null;
            foreach (var r in _spawnedRoots)
                if (!ReferenceEquals(r, node) && IsUnder(r, node))
                    (nested ??= []).Add(r);
            if (nested != null)
                foreach (var r in nested)
                    ExecuteDestroy(r);
        }

        var wasSpawned = _spawnedIds.Contains(node.Id);
        var parent = node.Parent;

        _scripts.DetachSubtree(node); // OnDisable/OnDestroy while the node is still in the tree
        _hooks?.OnDestroying(node);
        _ecs?.RemoveNode(node);
        UnregisterSubtree(node);
        _spatialDirty = true;

        if (wasSpawned)
            _spawnedRoots.Remove(node);
        else if (parent != null && _authoredRestoreIds.Add(node.Id))
            _authoredRestore.Add((node, parent, parent.Children.IndexOf(node)));

        node.RemoveFromNative();
        parent?.RemoveChild(node);
    }

    private void ExecuteReparent(SceneNode node, SceneNode newParent)
    {
        if (ReferenceEquals(node, Root)) return;
        if (ReferenceEquals(node.Parent, newParent)) return;
        if (IsUnder(newParent, node)) return; // refuse cycles

        if (!_spawnedIds.Contains(node.Id) && node.Parent is { } parent &&
            _authoredRestoreIds.Add(node.Id))
            _authoredRestore.Add((node, parent, parent.Children.IndexOf(node)));

        // There is no native reparent: free the subtree's native objects and let the next
        // SyncToNative recreate them under the new parent.
        node.RemoveFromNative();
        newParent.AddChild(node);
        _spatialDirty = true;
    }

    private SceneNode? FindByName(SceneNode node, string name)
    {
        if (node.Name == name && _nodes.ContainsKey(node.Id)) return node;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var found = FindByName(node.Children[i], name);
            if (found != null) return found;
        }

        return null;
    }

    private Component? FindComponentIn(SceneNode node, Type type)
    {
        var comps = _scripts.GetComponents(node.Id);
        for (var i = 0; i < comps.Count; i++)
            if (type.IsInstanceOfType(comps[i]))
                return comps[i];
        for (var i = 0; i < node.Children.Count; i++)
        {
            var found = FindComponentIn(node.Children[i], type);
            if (found != null) return found;
        }

        return null;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private bool TryNode(uint id, out SceneNode node)
    {
        if (_nodes.TryGetValue((int)id, out var n))
        {
            node = n;
            return true;
        }

        node = null!;
        return false;
    }

    private void RegisterSubtree(SceneNode node, bool spawned)
    {
        if (node.IsInternal) return; // editor gizmos are not gameplay entities

        _nodes[node.Id] = node;
        if (!string.IsNullOrEmpty(node.Tag)) _tags.Set(node.Id, node.Tag);
        if (spawned)
        {
            _spawnedIds.Add(node.Id);
            if (_ecs != null)
            {
                // Mirror into flecs like BuildFrom did for the authored tree: entity + ChildOf pair.
                var e = _ecs.EnsureEntity(node);
                if (node.Parent is { } parent)
                {
                    var pe = _ecs.EntityOf(parent.Id);
                    if (!pe.IsNull) _ecs.World.SetParent(e, pe);
                }
            }
        }

        for (var i = 0; i < node.Children.Count; i++) RegisterSubtree(node.Children[i], spawned);
    }

    private void UnregisterSubtree(SceneNode node)
    {
        _nodes.Remove(node.Id);
        _tags.Remove(node.Id);
        _spawnedIds.Remove(node.Id);
        for (var i = 0; i < node.Children.Count; i++) UnregisterSubtree(node.Children[i]);
    }

    private PrefabDocument? LoadPrefab(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path); // cwd = project dir (host convention)
        if (_prefabs.TryGetValue(full, out var cached)) return cached;

        PrefabDocument? doc = null;
        try
        {
            doc = PrefabDocument.Load(full);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[World] Failed to load prefab '{path}': {ex.Message}");
        }

        if (doc == null && File.Exists(full) is false)
            Console.Error.WriteLine($"[World] Prefab not found: '{path}' (resolved '{full}')");

        _prefabs[full] = doc;
        return doc;
    }

    private void EnsureSpatial()
    {
        if (!_spatialDirty && _spatialStamp == _tick) return;
        _spatial.Clear();
        foreach (var (id, node) in _nodes)
        {
            if (ReferenceEquals(node, Root)) continue; // the scene root is not a spatial hit
            _spatial.Insert(id, WorldTransform(node).Position);
        }

        _spatialStamp = _tick;
        _spatialDirty = false;
    }

    private static bool IsUnder(SceneNode node, SceneNode subtree)
    {
        for (var n = node; n != null; n = n.Parent)
            if (ReferenceEquals(n, subtree))
                return true;
        return false;
    }

    /// World transform of a node (parent-baked), matching how native composes node.world_transform.
    private static Transform3D WorldTransform(SceneNode node)
    {
        var local = new Transform3D(node.Position, node.Rotation, node.Scale);
        return node.Parent is { } parent
            ? Transform3D.Combine(WorldTransform(parent), local)
            : local;
    }
}