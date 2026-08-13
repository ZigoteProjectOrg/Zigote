using Xunit;
using Zigote.Runtime.Scene;
using Zigote.Vfx;

namespace Zigote.Tests;

/// <summary>
///     Pins scene → asset reachability (<see cref="AssetDependencyGraph" />): every path role a node
///     can
///     reference is collected, primitives and empties are not, and reverse lookup names the
///     dependents.
///     Game export stages exactly this set, so gaps here mean broken exported games.
/// </summary>
public class AssetDependencyGraphTests
{
    private static SceneGraph DemoScene()
    {
        var scene = new SceneGraph { EnvironmentPath = "assets/hdri/sky.hdr" };
        var car = new SceneNode(name: "Car", kind: NodeKind.Mesh) {
            MeshPath = "assets/models/car/.mesh_cache/body.zmesh",
            TexturePath = "assets/models/car/textures/body.png",
            MetallicRoughnessTexturePath = "assets/models/car/textures/body_mr.png",
            NormalTexturePath = "assets/models/car/textures/body_n.png",
            Parent = scene.Root,
        };
        scene.Root.Children.Add(car);

        var cube = new SceneNode(name: "Cube", kind: NodeKind.Mesh) {
            MeshPath = "#cube",
            Parent = scene.Root,
        };
        scene.Root.Children.Add(cube);

        var speaker = new SceneNode(name: "Speaker", kind: NodeKind.AudioSource) {
            AudioClipPath = "assets/audio/engine.ogg",
            Parent = scene.Root,
        };
        scene.Root.Children.Add(speaker);

        var sparks = new SceneNode(name: "Sparks", kind: NodeKind.VfxEmitter) {
            VfxBakedJson =
                VfxAssetJson.Serialize(
                    new VfxEmitterAsset { TexturePath = "assets/vfx/spark.png" }
                ),
            Parent = scene.Root,
        };
        scene.Root.Children.Add(sparks);

        return scene;
    }

    [Fact]
    public void Build_CollectsEveryReferencedFile()
    {
        var graph = AssetDependencyGraph.Build(DemoScene());

        string[] expected = [
            "assets/hdri/sky.hdr",
            "assets/models/car/.mesh_cache/body.zmesh",
            "assets/models/car/textures/body.png",
            "assets/models/car/textures/body_mr.png",
            "assets/models/car/textures/body_n.png",
            "assets/audio/engine.ogg",
            "assets/vfx/spark.png",
        ];
        Assert.Equal(expected: expected.OrderBy(p => p), actual: graph.Files.OrderBy(p => p));
    }

    [Fact]
    public void Build_SkipsPrimitivesAndEmpties()
    {
        var graph = AssetDependencyGraph.Build(DemoScene());
        Assert.DoesNotContain(expected: "#cube", collection: graph.Files);
    }

    [Fact]
    public void DependentsOf_NamesTheReferencingNode()
    {
        var graph = AssetDependencyGraph.Build(DemoScene());

        var dep = Assert.Single(graph.DependentsOf("assets/audio/engine.ogg"));
        Assert.Equal(expected: "Speaker", actual: dep.Node?.Name);
        Assert.Equal(expected: "audio", actual: dep.Role);

        var env = Assert.Single(graph.DependentsOf("assets/hdri/sky.hdr"));
        Assert.Null(env.Node);
        Assert.Equal(expected: "environment", actual: env.Role);

        Assert.Empty(graph.DependentsOf("assets/unused.png"));
    }

    [Fact]
    public void Build_PreservesPathSpellingOrdinally()
    {
        var scene = new SceneGraph();
        scene.Root.Children.Add(
            new SceneNode(name: "A", kind: NodeKind.Mesh) {
                MeshPath = "assets/Models/a.zmesh",
                Parent = scene.Root,
            }
        );
        scene.Root.Children.Add(
            new SceneNode(name: "B", kind: NodeKind.Mesh) {
                MeshPath = "assets/models/a.zmesh",
                Parent = scene.Root,
            }
        );

        // Distinct spellings stay distinct — staging must ship the path exactly as the runtime opens it.
        var graph = AssetDependencyGraph.Build(scene);
        Assert.Equal(expected: 2, actual: graph.Files.Count);
    }
}
