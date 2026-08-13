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
        var dir = Path.Combine(
            Path.GetTempPath(),
            "zigote-scenepaths",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Normalize_RewritesInProjectAbsolutes_AndKeepsForeignOnes()
    {
        var dir = TempDir();
        var warnings = new List<string>();
        var scene = new SceneGraph { EnvironmentPath = Path.Combine(dir, "assets", "sky.hdr") };
        scene.Root.Children.Add(
            new SceneNode("Car", NodeKind.Mesh) {
                MeshPath = Path.Combine(
                    dir,
                    "assets",
                    "models",
                    "car.zmesh"
                ),
                TexturePath = "assets/models/car.png", // already canonical — untouched
                NormalTexturePath =
                    "/outside/the/project.png", // foreign absolute — warned, untouched
                Parent = scene.Root,
            }
        );

        var count = ScenePaths.Normalize(scene, dir, warnings.Add);

        Assert.Equal(2, count);
        Assert.Equal("assets/sky.hdr", scene.EnvironmentPath);
        var car = scene.Root.Children[0];
        Assert.Equal("assets/models/car.zmesh", car.MeshPath);
        Assert.Equal("assets/models/car.png", car.TexturePath);
        Assert.Equal("/outside/the/project.png", car.NormalTexturePath);
        Assert.Contains(warnings, w => w.Contains("outside the project"));
    }

    [Fact]
    public void Normalize_ConvertsBackslashSeparators()
    {
        var dir = TempDir();
        var scene = new SceneGraph();
        scene.Root.Children.Add(
            new SceneNode("A", NodeKind.Mesh) {
                MeshPath = @"assets\models\a.zmesh",
                Parent = scene.Root,
            }
        );

        var count = ScenePaths.Normalize(scene, dir);

        Assert.Equal(1, count);
        Assert.Equal("assets/models/a.zmesh", scene.Root.Children[0].MeshPath);
    }

    [Fact]
    public void Normalize_LeavesPrimitivesAndMissingFilesAlone()
    {
        var dir = TempDir();
        var scene = new SceneGraph();
        scene.Root.Children.Add(
            new SceneNode("Cube", NodeKind.Mesh) {
                MeshPath = "#cube",
                Parent = scene.Root,
            }
        );
        scene.Root.Children.Add(
            new SceneNode("Ghost", NodeKind.Mesh) {
                MeshPath = "assets/missing/ghost.zmesh",
                Parent = scene.Root,
            }
        );

        var count = ScenePaths.Normalize(scene, dir);

        Assert.Equal(0, count);
        Assert.Equal("#cube", scene.Root.Children[0].MeshPath);
        Assert.Equal("assets/missing/ghost.zmesh", scene.Root.Children[1].MeshPath);
    }

    [Fact]
    public void AssetRegistry_NormalizesKeySeparators()
    {
        var registry = new AssetRegistry();
        var id = registry.Register(@"textures\stone.png");
        Assert.Equal(id, registry.Find("textures/stone.png"));
        Assert.Equal("textures/stone.png", registry.Resolve(id));
    }
}
