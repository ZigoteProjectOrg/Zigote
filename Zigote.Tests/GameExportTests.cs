using Samples.Scripting;
using Xunit;
using Zigote.Editor.Export;
using Zigote.Runtime.Content;
using Zigote.Runtime.Scene;
using Zigote.Scripting.Metadata;
using Zigote.Vfx;

namespace Zigote.Tests;

/// <summary>
///     Pins the pure halves of game export: the generated player project + static script registration,
///     and the VFX graph → baked asset scene rewrite. Publishing/packaging is exercised end-to-end by
///     build/export.sh, not here (no dotnet/zig invocation in unit tests).
/// </summary>
public class GameExportTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "zigote-export-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ExportInput Input(string dir, string? scriptProject = null,
        string? scriptAsm = null)
    {
        var project = new ZigoteProject {
            Name = "My Game",
            ScriptProject = scriptProject,
        };
        var projPath = Path.Combine(dir, "game.zigoteproj");
        project.Save(projPath);
        var registry = new ScriptRegistry();
        registry.Register(typeof(Rotator));
        registry.Register(typeof(CameraFollow));
        return new ExportInput(
            projPath,
            project,
            registry,
            scriptAsm
        );
    }

    [Fact]
    public void GeneratedRegistration_ListsComponentsSorted()
    {
        var dir = TempDir();
        GameExporter.GeneratePlayerProject(
            Input(dir),
            dir,
            Path.Combine(dir, "player"),
            "MyGame"
        );

        var reg = File.ReadAllText(Path.Combine(dir, "player", "ScriptRegistration.g.cs"));
        Assert.Contains("r.Register(typeof(global::Samples.Scripting.CameraFollow));", reg);
        Assert.Contains("r.Register(typeof(global::Samples.Scripting.Rotator));", reg);
        Assert.True(
            reg.IndexOf("CameraFollow", StringComparison.Ordinal) <
            reg.IndexOf("Rotator", StringComparison.Ordinal)
        );

        var program = File.ReadAllText(Path.Combine(dir, "player", "Program.g.cs"));
        Assert.Contains("PlayerMain.Run(GameScripts.Register)", program);
    }

    [Fact]
    public void GeneratedRegistration_TrimsUnreferencedSamples()
    {
        var dir = TempDir();
        // Scene references only Rotator; CameraFollow is an unreferenced engine sample → trimmed.
        GameExporter.GeneratePlayerProject(
            Input(dir),
            dir,
            Path.Combine(dir, "player"),
            "MyGame",
            new HashSet<string> { "Samples.Scripting.Rotator" }
        );

        var reg = File.ReadAllText(Path.Combine(dir, "player", "ScriptRegistration.g.cs"));
        Assert.Contains("r.Register(typeof(global::Samples.Scripting.Rotator));", reg);
        Assert.DoesNotContain("CameraFollow", reg);
    }

    [Fact]
    public void CollectScriptClasses_WalksTheTree()
    {
        var scene = new SceneGraph();
        var child = new SceneNode("Car", NodeKind.Mesh) {
            ScriptClass = "Game.CarController",
            Parent = scene.Root,
        };
        child.Children.Add(
            new SceneNode("Cam") {
                ScriptClass = "Game.ChaseCamera",
                Parent = child,
            }
        );
        scene.Root.Children.Add(child);

        var classes = GameExporter.CollectScriptClasses(scene);
        Assert.Equal(["Game.CarController", "Game.ChaseCamera"], classes.OrderBy(c => c));
    }

    [Fact]
    public void GeneratedCsproj_WiresPlayerAndScripts()
    {
        var dir = TempDir();
        GameExporter.GeneratePlayerProject(
            Input(dir, "scripts/Game.Scripts.csproj", "Game.Scripts"),
            dir,
            Path.Combine(dir, "player"),
            "MyGame"
        );

        var csproj = File.ReadAllText(Path.Combine(dir, "player", "Game.csproj"));
        Assert.Contains("<AssemblyName>MyGame</AssemblyName>", csproj);
        Assert.Contains("Zigote.Player.csproj", csproj);
        Assert.Contains("Game.Scripts.csproj", csproj);
        Assert.Contains("""<TrimmerRootAssembly Include="Game.Scripts" />""", csproj);
        Assert.Contains("""<TrimmerRootAssembly Include="Zigote.Runtime" />""", csproj);
        Assert.Contains("<PublishAot>true</PublishAot>", csproj);
    }

    [Fact]
    public void GeneratedCsproj_NoScriptProject_OmitsReference()
    {
        var dir = TempDir();
        GameExporter.GeneratePlayerProject(
            Input(dir),
            dir,
            Path.Combine(dir, "player"),
            "MyGame"
        );

        var csproj = File.ReadAllText(Path.Combine(dir, "player", "Game.csproj"));
        Assert.DoesNotContain("Scripts.csproj", csproj);
        Assert.Contains("Zigote.Player.csproj", csproj);
    }

    [Fact]
    public void BakeVfxGraphs_BakesEmitterAndClearsGraph()
    {
        var dir = TempDir();
        var scene = new SceneGraph();
        scene.Root.Children.Add(
            new SceneNode("Sparks", NodeKind.VfxEmitter) { Parent = scene.Root }
        );
        var path = Path.Combine(dir, "main.scene");
        scene.Save(path);

        GameExporter.BakeVfxGraphs(path, new NullProgress());

        var baked = SceneGraph.Load(path);
        var emitter = baked.Root.Children.Single(n => n.Kind == NodeKind.VfxEmitter);
        Assert.False(string.IsNullOrEmpty(emitter.VfxBakedJson));
        Assert.Null(emitter.VfxGraphJson);

        var asset = VfxAssetJson.Deserialize(emitter.VfxBakedJson!);
        Assert.True(asset.SpawnRate > 0f || asset.Bursts.Count > 0);
    }

    [Fact]
    public void ContentFiles_RoundTripsCompressed()
    {
        var dir = TempDir();
        var src = Path.Combine(dir, "blob.zmesh");
        var payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);
        // Mix in compressible structure so the codec has something to chew on.
        Array.Fill(
            payload,
            (byte)7,
            0,
            16 * 1024
        );
        File.WriteAllBytes(src, payload);

        var shipped = Path.Combine(dir, "shipped", "blob.zmesh");
        Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
        var compressedSize = ContentFiles.WriteCompressed(src, shipped);

        Assert.False(File.Exists(shipped)); // only the .zst variant ships
        Assert.True(File.Exists(shipped + ".zst"));
        Assert.True(compressedSize > 0 && compressedSize < payload.Length);

        Assert.True(ContentFiles.Exists(shipped));
        Assert.Equal(payload, ContentFiles.ReadAllBytes(shipped));

        // Plain files still read as-is (the editor's loose-file path).
        Assert.Equal(payload, ContentFiles.ReadAllBytes(src));
    }

    [Fact]
    public void BakeVfxGraphs_NoEmitters_LeavesSceneUntouched()
    {
        var dir = TempDir();
        var scene = new SceneGraph();
        scene.Root.Children.Add(new SceneNode("Cube", NodeKind.Mesh) { Parent = scene.Root });
        var path = Path.Combine(dir, "main.scene");
        scene.Save(path);
        var before = File.ReadAllBytes(path);

        GameExporter.BakeVfxGraphs(path, new NullProgress());

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private sealed class NullProgress : IProgress<string>
    {
        public void Report(string value)
        {
        }
    }
}
