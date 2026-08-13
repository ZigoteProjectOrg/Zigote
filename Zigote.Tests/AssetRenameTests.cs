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
        var mesh = new SceneNode(name: "Car", kind: NodeKind.Mesh) {
            MeshPath = "assets/models/car.zmesh",
            TexturePath = "Assets/Textures/stone.png", // different casing + separators still match
            NormalTexturePath = "assets/textures/stone_n.png",
        };
        var audio =
            new SceneNode(name: "Radio", kind: NodeKind.AudioSource) {
                AudioClipPath = "assets/audio/song.ogg",
            };
        scene.Root.AddChild(mesh);
        mesh.AddChild(audio);

        Assert.True(
            AssetReferenceRewriter.RewriteScene(
                scene: scene,
                oldRelativePath: "assets/textures/stone.png",
                newRelativePath: "assets/materials/stone.png"
            )
        );
        Assert.Equal(expected: "assets/materials/stone.png", actual: mesh.TexturePath);
        Assert.Equal(
            expected: "assets/textures/stone_n.png",
            actual: mesh.NormalTexturePath
        ); // untouched

        Assert.True(
            AssetReferenceRewriter.RewriteScene(
                scene: scene,
                oldRelativePath: "assets/audio/song.ogg",
                newRelativePath: "assets/audio/theme.ogg"
            )
        );
        Assert.Equal(expected: "assets/audio/theme.ogg", actual: audio.AudioClipPath);

        Assert.True(
            AssetReferenceRewriter.RewriteScene(
                scene: scene,
                oldRelativePath: "assets/sky.hdr",
                newRelativePath: "assets/env/sky.hdr"
            )
        );
        Assert.Equal(expected: "assets/env/sky.hdr", actual: scene.EnvironmentPath);

        Assert.False(
            AssetReferenceRewriter.RewriteScene(
                scene: scene,
                oldRelativePath: "assets/nothing.png",
                newRelativePath: "assets/x.png"
            )
        );
    }

    [Fact]
    public void Registry_RenameKeepsTheAssetId()
    {
        var registry = new AssetRegistry();
        var id = registry.Register("assets/textures/stone.png");

        registry.RenamePath(
            oldPath: "assets/textures/stone.png",
            newPath: "assets/materials/stone.png"
        );

        Assert.Equal(expected: "assets/materials/stone.png", actual: registry.Resolve(id));
        Assert.Equal(expected: id, actual: registry.Find("assets/materials/stone.png"));
        Assert.Null(registry.Find("assets/textures/stone.png"));
    }

    [Fact]
    public void DependencyGraph_ResolvesDependentsByAssetId()
    {
        var scene = new SceneGraph();
        scene.Root.AddChild(
            new SceneNode(name: "Car", kind: NodeKind.Mesh) { MeshPath = "assets/models/car.zmesh" }
        );
        var graph = AssetDependencyGraph.Build(scene);

        var registry = new AssetRegistry();
        var id = registry.Register("assets/models/car.zmesh");

        var dependents = graph.DependentsOf(id: id, registry: registry);
        Assert.Single(dependents);
        Assert.Equal(expected: "mesh", actual: dependents[0].Role);
        Assert.Empty(graph.DependentsOf(id: AssetId.New(), registry: registry));
    }
}
