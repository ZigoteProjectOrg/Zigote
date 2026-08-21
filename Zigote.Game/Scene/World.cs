using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Core.Physics;
using Zigote.Game.Resources;

namespace Zigote.Game.Scene;

public sealed class InputState
{
    public Vec2 MousePos { get; set; }
    public Vec2 MouseDelta { get; set; }
    public float ScrollY { get; set; }
    public bool LeftDown { get; set; }
    public bool RightDown { get; set; }
    public bool MiddleDown { get; set; }
}

/// <summary>
///     C# port of the Zig <c>World</c>. Owns the entire scene graph, all mesh and material
///     assets, and per-frame input state. Call <see cref="Update" /> once per frame and
///     <see cref="Sync" /> to push transforms to the Zig renderer.
/// </summary>
public sealed class World
{
    private readonly List<Material3D> _materials = [];
    private readonly List<Mesh3D> _meshes = [];
    private readonly List<SceneNode3D> _roots = [];

    // Reused flattened buffers for the batched physics→node transform sync (one FFI call per frame
    // instead of a position + rotation call pair per body; zero steady-state allocation).
    private readonly List<(SceneNode3D Node, uint BodyId)> _syncBodies = [];

    // Nodes with live physics bodies, indexed at AttachPhysics time (pruned in RemoveNode) so
    // SyncFromPhysics filters a flat list per frame instead of walking the whole scene graph —
    // the same index-over-tree fix GameSession applied to its body list.
    private readonly List<(SceneNode3D Node, RigidBody3D Body)> _physicsBodies = [];
    private ScratchBuffer<uint> _syncIds;
    private ScratchBuffer<float> _syncXforms;

    public IReadOnlyList<SceneNode3D> Roots => _roots;
    public IReadOnlyList<Mesh3D> Meshes => _meshes;
    public IReadOnlyList<Material3D> Materials => _materials;

    public SceneNode3D? ActiveCamera { get; set; }
    public SceneNode3D? ActiveCamera2D { get; set; }
    public Vec2 ViewportSize { get; set; } = new(x: 1, y: 1);
    public double ElapsedSeconds { get; private set; }
    public InputState Input { get; } = new();

    // ── Scene graph ───────────────────────────────────────────────────────────

    public SceneNode3D CreateNode(string name, Node3DKind kind = Node3DKind.Empty)
    {
        var node = new SceneNode3D(name: name, kind: kind) {
            Handle = ZigoteEngine.Instance!.SceneAddChildNode(
                parentHandle: 0,
                name: name,
                kind: (byte)kind
            ),
        };
        _roots.Add(node);
        return node;
    }

    public SceneNode3D CreateChild(SceneNode3D parent, string name,
        Node3DKind kind = Node3DKind.Empty)
    {
        var node = new SceneNode3D(name: name, kind: kind) {
            Handle = ZigoteEngine.Instance!.SceneAddChildNode(
                parentHandle: parent.Handle,
                name: name,
                kind: (byte)kind
            ),
        };
        parent.AddChild(node);
        return node;
    }

    public void RemoveNode(SceneNode3D node)
    {
        if (node.Handle != 0)
        {
            ZigoteEngine.Instance!.SceneRemoveNode(node.Handle);
            node.Handle = 0;
        }

        node.Parent?.RemoveChild(node);
        _roots.Remove(node);

        // Drop the removed subtree's entries from the flat body index (removal is rare; a scan
        // per removed node is fine).
        for (int i = _physicsBodies.Count - 1; i >= 0; i--)
        {
            for (var n = _physicsBodies[i].Node; n is not null; n = n.Parent)
            {
                if (ReferenceEquals(objA: n, objB: node))
                {
                    _physicsBodies.RemoveAt(i);
                    break;
                }
            }
        }
    }

    // ── Asset registries ──────────────────────────────────────────────────────

    public int AddMesh(Mesh3D mesh)
    {
        int handle = _meshes.Count;
        _meshes.Add(mesh);
        return handle;
    }

    public int AddMaterial(Material3D material)
    {
        int handle = _materials.Count;
        _materials.Add(material);
        return handle;
    }

    public Mesh3D? GetMesh(int handle) =>
        handle >= 0 && handle < _meshes.Count ? _meshes[handle] : null;

    public Material3D? GetMaterial(int handle) =>
        handle >= 0 && handle < _materials.Count ? _materials[handle] : null;

    // ── Per-frame update ─────────────────────────────────────────────────────

    public void Update(float dt)
    {
        ElapsedSeconds += dt;
        Input.MouseDelta = Vec2.Zero;
        Input.ScrollY = 0f;
        UpdateTransforms();
    }

    public void UpdateTransforms()
    {
        foreach (var root in _roots)
            root.UpdateWorldTransform();
    }

    // ── Zig renderer sync ────────────────────────────────────────────────────

    public void Sync()
    {
        // Indexed walk over the concrete root list + SceneNode3D.SyncTree — no Descendants() iterator
        // allocation per node per frame, and each node's Sync() now dirty-skips unchanged transforms.
        for (int i = 0; i < _roots.Count; i++)
            _roots[i].SyncTree();
    }

    // ── Physics ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Create JoltPhysics bodies for every <see cref="SceneNode3D" /> that has a
    ///     <see cref="RigidBody3D" /> component.  Call once after <see cref="GameApp.OnStart" />
    ///     has finished populating the scene, before the first <see cref="PhysicsWorld.Step" />.
    /// </summary>
    public void AttachPhysics(PhysicsWorld physics)
    {
        foreach (var root in _roots)
            AttachPhysicsNode(physics: physics, node: root);
        physics.OptimizeBroadPhase();

        // (Re)build the flat body index from scratch — AttachPhysics is the only place bodies are
        // created, so the index is complete until the next RemoveNode.
        _physicsBodies.Clear();
        foreach (var root in _roots)
            CollectPhysicsBodies(root);
    }

    private void CollectPhysicsBodies(SceneNode3D node)
    {
        if (node.RigidBody is { } rb && rb.BodyId != PhysicsWorld.InvalidBodyId)
            _physicsBodies.Add((node, rb));
        foreach (var child in node.Children)
            CollectPhysicsBodies(child);
    }

    private static void AttachPhysicsNode(PhysicsWorld physics, SceneNode3D node)
    {
        if (node is { Active: true, RigidBody: { BodyId: PhysicsWorld.InvalidBodyId } rb })
        {
            var motionType = rb.IsStatic ? PhysicsMotionType.Static : PhysicsMotionType.Dynamic;
            var euler = node.LocalTransform.Rotation.ToEulerRadians();
            rb.BodyId = physics.CreateAndAddBody(
                new PhysicsBodySettings {
                    ShapeType = rb.ShapeType,
                    HalfExtents = rb.HalfExtents,
                    Position = node.LocalTransform.Position,
                    Rotation = euler,
                    MotionType = motionType,
                    Friction = rb.Friction,
                    Restitution = rb.Restitution,
                }
            );
        }

        foreach (var child in node.Children)
            AttachPhysicsNode(physics: physics, node: child);
    }

    /// <summary>
    ///     Read back body transforms from <paramref name="physics" /> and apply them to
    ///     the corresponding scene nodes.  Call after each <see cref="PhysicsWorld.Step" />.
    /// </summary>
    public void SyncFromPhysics(PhysicsWorld physics)
    {
        _syncBodies.Clear();
        for (int i = 0; i < _physicsBodies.Count; i++)
        {
            (var node, var rb) = _physicsBodies[i];
            if (node.Active && !rb.IsStatic && rb.BodyId != PhysicsWorld.InvalidBodyId)
                _syncBodies.Add((node, rb.BodyId));
        }

        int count = _syncBodies.Count;
        if (count == 0) return;

        var ids = _syncIds.Get(count);
        for (int i = 0; i < count; i++) ids[i] = _syncBodies[i].BodyId;
        var xforms = _syncXforms.Get(count * 7);
        physics.GetBodyTransforms(ids: ids, outXforms: xforms);

        for (int i = 0; i < count; i++)
        {
            var node = _syncBodies[i].Node;
            int b = i * 7;
            node.Position = new Vec3(x: xforms[b], y: xforms[b + 1], z: xforms[b + 2]);
            node.Rotation = new Quat(
                x: xforms[b + 3],
                y: xforms[b + 4],
                z: xforms[b + 5],
                w: xforms[b + 6]
            );
        }
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public SceneNode3D? FindByName(string name)
    {
        foreach (var root in _roots)
        {
            if (root.Name == name) return root;
            var found = FindInTree(node: root, name: name);
            if (found is not null) return found;
        }

        return null;
    }

    public IEnumerable<SceneNode3D> CollectRenderables(RenderLayer layer)
    {
        foreach (var root in _roots)
        foreach (var node in AllNodes(root))
        {
            if (node is { Active: true, MeshRenderer: { Visible: true } mr } && mr.Layer == layer)
                yield return node;
        }
    }

    private static SceneNode3D? FindInTree(SceneNode3D node, string name)
    {
        foreach (var child in node.Children)
        {
            if (child.Name == name) return child;
            var found = FindInTree(node: child, name: name);
            if (found is not null) return found;
        }

        return null;
    }

    private static IEnumerable<SceneNode3D> AllNodes(SceneNode3D root)
    {
        yield return root;
        foreach (var c in root.Children)
        foreach (var n in AllNodes(c))
            yield return n;
    }
}
