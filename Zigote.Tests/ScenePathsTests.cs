using Xunit;
using Zigote.Core.Assets;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

/// <summary>
///     Pins the asset-path portability layer: scenes must reference content machine- and
///     OS-agnostically
///     (project-relative, forward slashes, on-disk casing).
///     <see cref="ScenePaths.Normalize(SceneGraph,string,Action{string}?)" />
///     runs on scene load / model import / prefab load / export, so regressions here mean scenes that
///     only open on the machine that authored them.
/// </summary>
public class ScenePathsTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "zigote-scenepaths",
            path3: Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Normalize_RewritesInProjectAbsolutes_AndKeepsForeignOnes()
    {
        string dir = TempDir();
        var warnings = new List<string>();
        var scene = new SceneGraph {
            EnvironmentPath = Path.Combine(path1: dir, path2: "assets", path3: "sky.hdr"),
        };
        scene.Root.Children.Add(
            new SceneNode(name: "Car", kind: NodeKind.Mesh) {
                MeshPath = Path.Combine(
                    path1: dir,
                    path2: "assets",
                    path3: "models",
                    path4: "car.zmesh"
                ),
                TexturePath = "assets/models/car.png", // already canonical — untouched
                NormalTexturePath =
                    "/outside/the/project.png", // foreign absolute — warned, untouched
                Parent = scene.Root,
            }
        );

        int count = ScenePaths.Normalize(scene: scene, projectRoot: dir, warn: warnings.Add);

        Assert.Equal(expected: 2, actual: count);
        Assert.Equal(expected: "assets/sky.hdr", actual: scene.EnvironmentPath);
        var car = scene.Root.Children[0];
        Assert.Equal(expected: "assets/models/car.zmesh", actual: car.MeshPath);
        Assert.Equal(expected: "assets/models/car.png", actual: car.TexturePath);
        Assert.Equal(expected: "/outside/the/project.png", actual: car.NormalTexturePath);
        Assert.Contains(collection: warnings, filter: w => w.Contains("outside the project"));
    }

    [Fact]
    public void Normalize_ConvertsBackslashSeparators()
    {
        string dir = TempDir();
        var scene = new SceneGraph();
        scene.Root.Children.Add(
            new SceneNode(name: "A", kind: NodeKind.Mesh) {
                MeshPath = @"assets\models\a.zmesh",
                Parent = scene.Root,
            }
        );

        int count = ScenePaths.Normalize(scene: scene, projectRoot: dir);

        Assert.Equal(expected: 1, actual: count);
        Assert.Equal(expected: "assets/models/a.zmesh", actual: scene.Root.Children[0].MeshPath);
    }

    [Fact]
    public void Normalize_LeavesPrimitivesAndMissingFilesAlone()
    {
        string dir = TempDir();
        var scene = new SceneGraph();
        scene.Root.Children.Add(
            new SceneNode(name: "Cube", kind: NodeKind.Mesh) {
                MeshPath = "#cube",
                Parent = scene.Root,
            }
        );
        scene.Root.Children.Add(
            new SceneNode(name: "Ghost", kind: NodeKind.Mesh) {
                MeshPath = "assets/missing/ghost.zmesh",
                Parent = scene.Root,
            }
        );

        int count = ScenePaths.Normalize(scene: scene, projectRoot: dir);

        Assert.Equal(expected: 0, actual: count);
        Assert.Equal(expected: "#cube", actual: scene.Root.Children[0].MeshPath);
        Assert.Equal(
            expected: "assets/missing/ghost.zmesh",
            actual: scene.Root.Children[1].MeshPath
        );
    }

    [Fact]
    public void AssetRegistry_NormalizesKeySeparators()
    {
        var registry = new AssetRegistry();
        var id = registry.Register(@"textures\stone.png");
        Assert.Equal(expected: id, actual: registry.Find("textures/stone.png"));
        Assert.Equal(expected: "textures/stone.png", actual: registry.Resolve(id));
    }
}
