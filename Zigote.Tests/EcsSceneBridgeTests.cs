using Xunit;
using Zigote.Core.Math3D;
using Zigote.Ecs.Scene;

namespace Zigote.Tests;

/// <summary>
///     The SceneNode↔entity bridge + transform hand-off (Option-A play spine), tested headlessly via a
///     fake node tree (Zigote.Tests cannot reference the editor where the real SceneNode lives).
/// </summary>
public sealed class EcsSceneBridgeTests : IDisposable
{
    private readonly EcsSceneBridge _bridge = new();

    public void Dispose() => _bridge.Dispose();

    [Fact]
    public void BuildFrom_Creates_An_Entity_Per_Node_With_Reverse_Lookup()
    {
        var root = Node(
            id: 1,
            name: "root",
            pos: new Vec3(x: 1, y: 0, z: 0),
            Child(id: 2, name: "a"),
            Child(id: 3, name: "b")
        );
        _bridge.BuildFrom(root);

        Assert.Equal(expected: 3, actual: _bridge.NodeEntities.Count);
        var e = _bridge.EntityOf(1);
        Assert.False(e.IsNull);
        Assert.True(_bridge.TryNodeId(e: e, nodeId: out int nodeId));
        Assert.Equal(expected: 1, actual: nodeId);
    }

    [Fact]
    public void BuildFrom_Seeds_The_Transform_From_The_Node()
    {
        var root = Node(id: 1, name: "root", pos: new Vec3(x: 2, y: 3, z: 4));
        _bridge.BuildFrom(root);

        Assert.True(_bridge.TryGetTransform(nodeId: 1, transform: out var t));
        Assert.Equal(expected: new Vec3(x: 2, y: 3, z: 4), actual: t.Position);
    }

    [Fact]
    public void BuildFrom_Mirrors_Hierarchy_As_Flecs_ChildOf()
    {
        var root = Node(
            id: 1,
            name: "root",
            pos: Vec3.Zero,
            Child(id: 2, name: "child")
        );
        _bridge.BuildFrom(root);

        var parent = _bridge.EntityOf(1);
        var child = _bridge.EntityOf(2);
        Assert.Equal(expected: parent, actual: _bridge.World.GetParent(child));
    }

    [Fact]
    public void PullTransforms_Mirrors_Canonical_Entity_Transform_Back_Onto_The_Node()
    {
        var node = Node(id: 1, name: "n", pos: Vec3.Zero);
        _bridge.BuildFrom(node);

        // Simulate physics/scripts writing the CANONICAL entity transform during play.
        _bridge.SetTransform(
            nodeId: 1,
            transform: new Transform {
                Position = new Vec3(x: 9, y: 8, z: 7),
                Rotation = Quat.Identity,
                Scale = Vec3.One,
            }
        );

        Assert.Equal(expected: Vec3.Zero, actual: node.Position); // node not yet mirrored
        _bridge.PullTransforms(node);
        Assert.Equal(
            expected: new Vec3(x: 9, y: 8, z: 7),
            actual: node.Position
        ); // now mirrored for rendering
    }

    [Fact]
    public void PushTransforms_Bakes_Authored_Node_Transform_Into_The_Entity()
    {
        var node = Node(id: 1, name: "n", pos: new Vec3(x: 1, y: 1, z: 1));
        _bridge.BuildFrom(node);

        node.Position = new Vec3(x: 5, y: 5, z: 5); // author edits the node
        _bridge.PushTransforms(node);

        Assert.True(_bridge.TryGetTransform(nodeId: 1, transform: out var t));
        Assert.Equal(expected: new Vec3(x: 5, y: 5, z: 5), actual: t.Position);
    }

    [Fact]
    public void RemoveNode_Destroys_The_Subtree_Entities()
    {
        var root = Node(
            id: 1,
            name: "root",
            pos: Vec3.Zero,
            Child(id: 2, name: "a", Child(id: 3, name: "a.1"))
        );
        _bridge.BuildFrom(root);
        var childEntity = _bridge.EntityOf(2);
        var grandchild = _bridge.EntityOf(3);

        _bridge.RemoveNode(root.Children[0]); // remove "a" + "a.1"

        Assert.True(_bridge.EntityOf(2).IsNull);
        Assert.True(_bridge.EntityOf(3).IsNull);
        Assert.False(_bridge.World.IsAlive(childEntity));
        Assert.False(_bridge.World.IsAlive(grandchild));
        Assert.False(_bridge.TryNodeId(e: childEntity, nodeId: out _));
    }

    [Fact]
    public void Duplicate_Node_Names_Get_Distinct_Entities_Without_Cycling()
    {
        // The editor allows duplicate names (adding items yields "Empty", "Empty", …). flecs entity names
        // are unique per scope, so naming entities would collapse these onto one entity and then self-parent
        // it via SetParent → flecs aborts (SIGABRT). This is the "add item + play" crash; entities are
        // unnamed, so each node is distinct and the hierarchy is intact.
        var root = Node(
            id: 1,
            name: "Empty",
            pos: Vec3.Zero,
            Child(id: 2, name: "Empty"),
            Child(id: 3, name: "Empty")
        );
        _bridge.BuildFrom(root); // must not abort

        Assert.Equal(expected: 3, actual: _bridge.NodeEntities.Count);
        Assert.NotEqual(expected: _bridge.EntityOf(1), actual: _bridge.EntityOf(2));
        Assert.NotEqual(expected: _bridge.EntityOf(2), actual: _bridge.EntityOf(3));
        Assert.Equal(
            expected: _bridge.EntityOf(1),
            actual: _bridge.World.GetParent(_bridge.EntityOf(2))
        );
        Assert.Equal(
            expected: _bridge.EntityOf(1),
            actual: _bridge.World.GetParent(_bridge.EntityOf(3))
        );
    }

    [Fact]
    public void Simulated_Play_Frame_RoundTrips_Through_The_Entity_As_Source_Of_Truth()
    {
        var node = Node(id: 1, name: "n", pos: new Vec3(x: 0, y: 0, z: 0));
        _bridge.BuildFrom(node); // play start: scene → entity

        // One play tick: "physics" advances the canonical entity transform...
        var t = new Transform {
            Position = new Vec3(x: 0, y: 10, z: 0),
            Rotation = Quat.Identity,
            Scale = Vec3.One,
        };
        _bridge.SetTransform(nodeId: 1, transform: t);
        // ...then the render mirror copies it back onto the node the renderer reads.
        _bridge.PullTransforms(node);

        Assert.Equal(expected: 10f, actual: node.Position.Y);
    }

    // ── fake node ────────────────────────────────────────────────────────────────
    private static FakeNode Node(int id, string name, Vec3 pos, params FakeNode[] children)
    {
        return new FakeNode {
            Id = id,
            Name = name,
            Position = pos,
            Children = [.. children],
        };
    }

    private static FakeNode Child(int id, string name, params FakeNode[] children)
    {
        return Node(
            id: id,
            name: name,
            pos: Vec3.Zero,
            children: children
        );
    }

    private sealed class FakeNode : IEcsSceneNode
    {
        public List<FakeNode> Children { get; init; } = [];
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public Vec3 Position { get; set; } = Vec3.Zero;
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Scale { get; set; } = Vec3.One;
        IReadOnlyList<IEcsSceneNode> IEcsSceneNode.Children => Children;
    }
}
