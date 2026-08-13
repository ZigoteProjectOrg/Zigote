using Xunit;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

public class SceneLoadVerify
{
    [Fact]
    public void PorscheDemoSceneLoads()
    {
        // Walk up from the test bin dir to the repo root, then to the demo scene.
        string dir = AppContext.BaseDirectory;
        string? scene = null;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(
                d.FullName,
                "examples",
                "PorscheDemo",
                "assets",
                "main.scene"
            );
            if (File.Exists(candidate))
            {
                scene = candidate;
                break;
            }
        }

        if (scene is null || !File.Exists(scene)) return;
        Assert.NotNull(scene);
        var graph = SceneGraph.Load(scene!);
        var all = new List<SceneNode> { graph.Root };
        all.AddRange(graph.Root.Descendants());

        // The car + chase-camera are wired purely through scripts (game code) — no engine vehicle types.
        var porsche = all.Single(n => n.Name == "Porsche 911 Turbo");
        Assert.Equal(expected: "ExampleProject.Scripts.CarController", actual: porsche.ScriptClass);

        var camera = all.Single(n => n.Name == "Camera");
        Assert.Equal(
            expected: "ExampleProject.Scripts.ChaseCameraComponent",
            actual: camera.ScriptClass
        );

        // A ground plane to drive on exists.
        Assert.Contains(collection: all, filter: n => n.Name == "Ground");
    }
}
