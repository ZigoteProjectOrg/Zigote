using Xunit;
using Zigote.Core.Math3D;
using Zigote.Ecs;
using Zigote.Ecs.Scene;

namespace Zigote.Tests;

/// <summary>
///     The change-gated scene→entity transform push (an unchanged node skips the FFI write, so an
///     entity-side write survives the next push) and the SystemCount hosts use to skip the pull half.
/// </summary>
public sealed class EcsTransformGateTests : IDisposable
{
    private readonly EcsSceneBridge _bridge = new();

    public void Dispose() => _bridge.Dispose();

    [Fact]
    public void PushTransforms_Skips_The_Write_For_An_Unchanged_Node()
    {
        var node = Node(id: 1, pos: new Vec3(x: 1, y: 2, z: 3));
        _bridge.BuildFrom(node);
        _bridge.PushTransforms(node); // records the per-node gate

        // An entity-side write (a system / script) must survive a push of the UNCHANGED node —
        // the gate skips the Set instead of clobbering the canonical entity transform.
        _bridge.SetTransform(
            nodeId: 1,
            transform: new Transform {
                Position = new Vec3(x: 9, y: 9, z: 9),
                Rotation = Quat.Identity,
                Scale = Vec3.One,
            }
        );
        _bridge.PushTransforms(node);

        Assert.True(_bridge.TryGetTransform(nodeId: 1, transform: out var t));
        Assert.Equal(expected: new Vec3(x: 9, y: 9, z: 9), actual: t.Position);
    }

    [Fact]
    public void PushTransforms_Writes_When_The_Node_Moved()
    {
        var node = Node(id: 1, pos: new Vec3(x: 1, y: 2, z: 3));
        _bridge.BuildFrom(node);
        _bridge.PushTransforms(node);

        node.Position = new Vec3(x: 5, y: 5, z: 5);
        _bridge.PushTransforms(node);

        Assert.True(_bridge.TryGetTransform(nodeId: 1, transform: out var t));
        Assert.Equal(expected: new Vec3(x: 5, y: 5, z: 5), actual: t.Position);
    }

    [Fact]
    public void PullTransforms_Keeps_The_Gate_Accurate_For_The_Next_Push()
    {
        var node = Node(id: 1, pos: Vec3.Zero);
        _bridge.BuildFrom(node);
        _bridge.PushTransforms(node);

        _bridge.SetTransform(
            nodeId: 1,
            transform: new Transform {
                Position = new Vec3(x: 0, y: 10, z: 0),
                Rotation = Quat.Identity,
                Scale = Vec3.One,
            }
        );
        _bridge.PullTransforms(node); // node now mirrors the entity (0,10,0)

        node.Position = new Vec3(x: 0, y: 20, z: 0); // author/script moves the node afterwards
        _bridge.PushTransforms(node);

        Assert.True(_bridge.TryGetTransform(nodeId: 1, transform: out var t));
        Assert.Equal(expected: new Vec3(x: 0, y: 20, z: 0), actual: t.Position);
    }

    [Fact]
    public void RegisterSystem_And_RegisterObserver_Increment_SystemCount()
    {
        using var w = new EcsWorld();
        Assert.Equal(expected: 0, actual: w.SystemCount);

        w.RegisterSystem<Transform>(
            name: "NoopSystem",
            phase: EcsPhase.OnUpdate,
            body: _ => { }
        );
        Assert.Equal(expected: 1, actual: w.SystemCount);

        w.RegisterObserver<Transform>(
            name: "NoopObserver",
            evt: EcsEvent.OnSet,
            body: (_, _) => { }
        );
        Assert.Equal(expected: 2, actual: w.SystemCount);
    }

    // ── fake node ────────────────────────────────────────────────────────────────
    private static FakeNode Node(int id, Vec3 pos)
    {
        return new FakeNode {
            Id = id,
            Position = pos,
        };
    }

    private sealed class FakeNode : IEcsSceneNode
    {
        public List<FakeNode> Children { get; } = [];
        public int Id { get; init; }
        public string Name { get; } = "";
        public Vec3 Position { get; set; } = Vec3.Zero;
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Scale { get; set; } = Vec3.One;
        IReadOnlyList<IEcsSceneNode> IEcsSceneNode.Children => Children;
    }
}
