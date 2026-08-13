using Xunit;
using Zigote.Core.Math3D;
using Zigote.Ecs.Prefab;
using Zigote.Ecs.Reflection;
using Zigote.Ecs.Scene;
// Alias: under the Zigote.* tree the bare name `Ecs` binds to the namespace Zigote.Ecs, shadowing the
// provider class. User scripts (own namespace) just write `Ecs`; Zigote-namespaced code qualifies it.
using EcsApi = Zigote.Scripting.Ecs;

namespace Zigote.Tests;

/// <summary>
///     The scripting <see cref="Ecs" /> provider — the entity-first counterpart to Physics/Audio. Verifies
///     it is a safe no-op outside play and exposes the live world / scene bridge / prefab library when the
///     host wires it (as GameSession does).
/// </summary>
public sealed class EcsProviderTests : IDisposable
{
    public void Dispose()
    {
        EcsApi.World?.Dispose(); // we created the world the provider points at
        EcsApi.World = null;
        EcsApi.Scene = null;
        EcsApi.Prefabs = null;
    }

    [Fact]
    public void Outside_Play_Everything_Is_A_Safe_NoOp()
    {
        EcsApi.World = null;
        EcsApi.Scene = null;
        EcsApi.Prefabs = null;

        Assert.False(EcsApi.IsAvailable);
        Assert.True(EcsApi.CreateEntity().IsNull);
        Assert.True(EcsApi.Instantiate("missing").IsNull);
        Assert.True(EcsApi.EntityForNode(1).IsNull);
    }

    [Fact]
    public void Wired_Provider_Exposes_World_And_Prefabs()
    {
        var bridge = new EcsSceneBridge(); // owns a flecs world
        EcsApi.World = bridge.World;
        EcsApi.Scene = bridge;
        EcsApi.Prefabs = new EcsPrefabLibrary(bridge.World, new EcsComponentRegistry());

        Assert.True(EcsApi.IsAvailable);
        Assert.False(EcsApi.CreateEntity().IsNull);

        EcsApi.Prefabs.Define("Pickup").With(new Tag { Value = 7 });
        var inst = EcsApi.Instantiate("Pickup");
        Assert.False(inst.IsNull);
        Assert.True(EcsApi.World!.TryGet<Tag>(inst, out var tag));
        Assert.Equal(7, tag.Value);
    }

    [Fact]
    public void EntityForNode_Resolves_Through_The_Scene_Bridge()
    {
        var bridge = new EcsSceneBridge();
        bridge.BuildFrom(new TinyNode { Id = 42 });
        EcsApi.World = bridge.World;
        EcsApi.Scene = bridge;

        var e = EcsApi.EntityForNode(42);
        Assert.False(e.IsNull);
        Assert.Equal(bridge.EntityOf(42), e);
    }

    private struct Tag
    {
        public int Value;
    }

    private sealed class TinyNode : IEcsSceneNode
    {
        public int Id { get; init; }
        public string Name => "n";
        public Vec3 Position { get; set; } = Vec3.Zero;
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Scale { get; set; } = Vec3.One;
        public IReadOnlyList<IEcsSceneNode> Children => [];
    }
}
