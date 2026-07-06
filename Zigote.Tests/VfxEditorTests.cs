using Xunit;
using Zigote.Core.Math3D;
using Zigote.Editor.Vfx;
using Zigote.Graphs.Vfx;
using Zigote.Runtime.Scene;
using Zigote.Runtime.Vfx;

namespace Zigote.Tests;

/// <summary>
///     Editor-integration tests for VFX that stay headless: graph (de)serialization, the node↔graph glue,
///     and the play-mode CPU playback manager. None touch the native engine.
/// </summary>
public class VfxEditorTests
{
    [Fact]
    public void GraphSerializer_RoundTripsPreset()
    {
        var graph = VfxPresets.Create("Fire", "Fire");
        var back = VfxGraphSerializer.Deserialize(VfxGraphSerializer.Serialize(graph));

        Assert.Equal(graph.Nodes.Count, back.Nodes.Count);
        Assert.Equal(graph.Edges.Count, back.Edges.Count);

        var a = VfxGraphCompiler.Compile(graph).Asset;
        var b = VfxGraphCompiler.Compile(back).Asset;
        Assert.Equal(a.SpawnRate, b.SpawnRate, 4);
        Assert.Equal(a.Blend, b.Blend);
        Assert.Equal(a.Shape, b.Shape);
        Assert.Equal(a.UpdateModules.Count, b.UpdateModules.Count);
    }

    [Fact]
    public void NodeEditor_SeedsAndCompilesNode()
    {
        var node = new SceneNode("VFX", NodeKind.VfxEmitter);
        Assert.True(string.IsNullOrEmpty(node.VfxGraphJson));

        VfxNodeEditor.SeedDefault(node);
        Assert.False(string.IsNullOrEmpty(node.VfxGraphJson));

        var compiled = VfxNodeEditor.Compile(node);
        Assert.True(compiled.Success);
        Assert.True(compiled.Asset.SpawnRate > 0f);
    }

    [Fact]
    public void NodeEditor_NoGraph_FallsBackToDefaultPreset()
    {
        var node = new SceneNode("VFX", NodeKind.VfxEmitter);
        var compiled = VfxNodeEditor.Compile(node);
        Assert.True(compiled.Success);
        Assert.NotEmpty(compiled.Asset.UpdateModules);
    }

    [Fact]
    public void ScenePlayback_BuildsAndSpawns()
    {
        var root = new SceneNode("Root");
        var emitter = new SceneNode("VFX", NodeKind.VfxEmitter) { Position = new Vec3(0f, 0f, 0f) };
        VfxNodeEditor.SeedDefault(emitter);
        root.AddChild(emitter);

        var playback = new VfxScenePlayback();
        playback.Build(root);
        Assert.Single(playback.Emitters);

        for (var i = 0; i < 60; i++) playback.Step(1f / 60f);
        Assert.True(playback.Emitters[0].sim.Pool.Count > 0);
    }

    [Fact]
    public void ScenePlayback_RespectsPlayOnStart()
    {
        var root = new SceneNode("Root");
        var emitter = new SceneNode("VFX", NodeKind.VfxEmitter) { VfxPlayOnStart = false };
        VfxNodeEditor.SeedDefault(emitter);
        root.AddChild(emitter);

        var playback = new VfxScenePlayback();
        playback.Build(root);
        for (var i = 0; i < 60; i++) playback.Step(1f / 60f);
        Assert.Equal(0, playback.Emitters[0].sim.Pool.Count);
    }
}