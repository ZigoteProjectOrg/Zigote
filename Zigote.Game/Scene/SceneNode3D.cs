using Zigote.Core.Engine;
using Zigote.Core.Math3D;

namespace Zigote.Game.Scene;

/// <summary>
///     A node in the 3D scene graph. Holds a <see cref="Transform3D" /> and a list of typed
///     components.
///     <see cref="Sync" /> pushes the current transform to the Zig renderer each frame.
/// </summary>
public sealed class SceneNode3D(string name, Node3DKind kind = Node3DKind.Empty)
{
    private readonly List<SceneNode3D> _children = [];

    // Last world transform pushed to native; Sync() skips the FFI crossing when it is unchanged.
    private Vec3 _pPos;
    private Quat _pRot;
    private Vec3 _pScale;
    private bool _pushed;

    public string Name { get; set; } = name;
    public Node3DKind Kind { get; set; } = kind;
    public SceneNode3D? Parent { get; private set; }
    public bool Active { get; set; } = true;

    public Transform3D LocalTransform { get; set; } = Transform3D.Identity;
    public Transform3D WorldTransform { get; private set; } = Transform3D.Identity;

    // Convenience accessors that forward to LocalTransform.
    public Vec3 Position
    {
        get => LocalTransform.Position;
        set => LocalTransform = new Transform3D(
            value,
            LocalTransform.Rotation,
            LocalTransform.Scale
        );
    }

    public Quat Rotation
    {
        get => LocalTransform.Rotation;
        set => LocalTransform = new Transform3D(
            LocalTransform.Position,
            value,
            LocalTransform.Scale
        );
    }

    public Vec3 Scale
    {
        get => LocalTransform.Scale;
        set => LocalTransform = new Transform3D(
            LocalTransform.Position,
            LocalTransform.Rotation,
            value
        );
    }

    // Typed components.
    public Camera3D? Camera { get; set; }
    public MeshRenderer3D? MeshRenderer { get; set; }
    public Light3D? Light { get; set; }
    public RigidBody3D? RigidBody { get; set; }

    // Optional asset paths (serialisation / editor use).
    public string? MeshPath { get; set; }
    public string? ScriptPath { get; set; }

    // Zig renderer handle (0 = not yet registered).
    public ulong Handle { get; set; }

    // ── Hierarchy ─────────────────────────────────────────────────────────────

    public IReadOnlyList<SceneNode3D> Children => _children;

    public void AddChild(SceneNode3D child)
    {
        child.Parent?.RemoveChild(child);
        child.Parent = this;
        _children.Add(child);
    }

    public void RemoveChild(SceneNode3D child)
    {
        if (_children.Remove(child)) child.Parent = null;
    }

    public IEnumerable<SceneNode3D> Descendants()
    {
        foreach (var c in _children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    // ── Transform propagation ─────────────────────────────────────────────────

    public void UpdateWorldTransform()
    {
        WorldTransform = Parent is null
            ? LocalTransform
            : Transform3D.Combine(Parent.WorldTransform, LocalTransform);

        foreach (var child in _children)
            child.UpdateWorldTransform();
    }

    public Mat4 WorldMatrix()
    {
        return WorldTransform.ToMat4();
    }

    // ── Zig FFI sync ─────────────────────────────────────────────────────────

    public void Sync()
    {
        if (Handle == 0) return;
        var pos = WorldTransform.Position;
        var rot = WorldTransform.Rotation;
        var scale = WorldTransform.Scale;
        // Skip the FFI crossing for nodes whose world transform hasn't moved since the last push —
        // a static prop costs one zigote_scene_update_node call at startup, then nothing per frame.
        // Tolerant compare (ApproxEquals) so sub-tolerance float drift doesn't re-sync every frame;
        // rotation uses exact Quat equality (Quat.Equals is exact).
        if (_pushed && pos.ApproxEquals(_pPos) && rot == _pRot &&
            scale.ApproxEquals(_pScale)) return;
        ZigoteEngine.Instance!.SceneUpdateNode(
            Handle,
            pos.X,
            pos.Y,
            pos.Z,
            rot.X,
            rot.Y,
            rot.Z,
            rot.W,
            scale.X,
            scale.Y,
            scale.Z
        );
        _pPos = pos;
        _pRot = rot;
        _pScale = scale;
        _pushed = true;
    }

    /// <summary>
    ///     Recursively pushes this node's transform (when active) and every descendant's to native,
    ///     iterating the concrete child list directly — no per-node iterator/closure allocation.
    ///     Matches <c>World.Sync</c>'s prior semantics: sync a node only when active, but always
    ///     recurse into its children (an inactive parent doesn't prune active descendants).
    /// </summary>
    public void SyncTree()
    {
        if (Active) Sync();
        for (var i = 0; i < _children.Count; i++)
            _children[i].SyncTree();
    }
}