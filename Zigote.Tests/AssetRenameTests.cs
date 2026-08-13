using Xunit;
using Zigote.Core.Assets;
using Zigote.Editor.Assets;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

/// <summary>Rename-awareness: the registry keeps its AssetId and the scene rewriter heals references.</summary>
public class AssetRenameTests
{
    [Fact]
    public void Rewrite_HealsEveryPathField_CaseInsensitively()
    {
        var scene = new SceneGraph { EnvironmentPath = "assets/sky.hdr" };
        var mesh = new SceneNode("Car", NodeKind.Mesh) {
            MeshPath = "assets/models/car.zmesh",
            TexturePath = "Assets/Textures/stone.png", // different casing + separators still match
            NormalTexturePath = "assets/textures/stone_n.png",
        };
        var audio =
            new SceneNode("Radio", NodeKind.AudioSource) {
                AudioClipPath = "assets/audio/song.ogg",
            };
        scene.Root.AddChild(mesh);
        mesh.AddChild(audio);

        Assert.True(
            AssetReferenceRewriter.RewriteScene(
                scene,
                "assets/textures/stone.png",
                "assets/materials/stone.png"
            )
        );
        Assert.Equal("assets/materials/stone.png", mesh.TexturePath);
        Assert.Equal("assets/textures/stone_n.png", mesh.NormalTexturePath); // untouched

        Assert.True(
            AssetReferenceRewriter.RewriteScene(
                scene,
                "assets/audio/song.ogg",
                "assets/audio/theme.ogg"
            )
        );
        Assert.Equal("assets/audio/theme.ogg", audio.AudioClipPath);

        Assert.True(
            AssetReferenceRewriter.RewriteScene(scene, "assets/sky.hdr", "assets/env/sky.hdr")
        );
        Assert.Equal("assets/env/sky.hdr", scene.EnvironmentPath);

        Assert.False(
            AssetReferenceRewriter.RewriteScene(scene, "assets/nothing.png", "assets/x.png")
        );
    }

    [Fact]
    public void Registry_RenameKeepsTheAssetId()
    {
        var registry = new AssetRegistry();
        var id = registry.Register("assets/textures/stone.png");

        registry.RenamePath("assets/textures/stone.png", "assets/materials/stone.png");

        Assert.Equal("assets/materials/stone.png", registry.Resolve(id));
        Assert.Equal(id, registry.Find("assets/materials/stone.png"));
        Assert.Null(registry.Find("assets/textures/stone.png"));
    }

    [Fact]
    public void DependencyGraph_ResolvesDependentsByAssetId()
    {
        var scene = new SceneGraph();
        scene.Root.AddChild(
            new SceneNode("Car", NodeKind.Mesh) { MeshPath = "assets/models/car.zmesh" }
        );
        var graph = AssetDependencyGraph.Build(scene);

        var registry = new AssetRegistry();
        var id = registry.Register("assets/models/car.zmesh");

        var dependents = graph.DependentsOf(id, registry);
        Assert.Single(dependents);
        Assert.Equal("mesh", dependents[0].Role);
        Assert.Empty(graph.DependentsOf(AssetId.New(), registry));
    }
}
