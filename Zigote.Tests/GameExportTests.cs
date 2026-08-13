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
        string dir = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "zigote-export-tests",
            path3: Guid.NewGuid().ToString("N")
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
        string projPath = Path.Combine(path1: dir, path2: "game.zigoteproj");
        project.Save(projPath);
        var registry = new ScriptRegistry();
        registry.Register(typeof(Rotator));
        registry.Register(typeof(CameraFollow));
        return new ExportInput(
            ProjectPath: projPath,
            Project: project,
            Scripts: registry,
            ScriptAssemblyName: scriptAsm
        );
    }

    [Fact]
    public void GeneratedRegistration_ListsComponentsSorted()
    {
        string dir = TempDir();
        GameExporter.GeneratePlayerProject(
            input: Input(dir),
            sdkRoot: dir,
            playerDir: Path.Combine(path1: dir, path2: "player"),
            exeName: "MyGame"
        );

        string reg = File.ReadAllText(
            Path.Combine(path1: dir, path2: "player", path3: "ScriptRegistration.g.cs")
        );
        Assert.Contains(
            expectedSubstring: "r.Register(typeof(global::Samples.Scripting.CameraFollow));",
            actualString: reg
        );
        Assert.Contains(
            expectedSubstring: "r.Register(typeof(global::Samples.Scripting.Rotator));",
            actualString: reg
        );
        Assert.True(
            reg.IndexOf(value: "CameraFollow", comparisonType: StringComparison.Ordinal) <
            reg.IndexOf(value: "Rotator", comparisonType: StringComparison.Ordinal)
        );

        string program = File.ReadAllText(
            Path.Combine(path1: dir, path2: "player", path3: "Program.g.cs")
        );
        Assert.Contains(
            expectedSubstring: "PlayerMain.Run(GameScripts.Register)",
            actualString: program
        );
    }

    [Fact]
    public void GeneratedRegistration_TrimsUnreferencedSamples()
    {
        string dir = TempDir();
        // Scene references only Rotator; CameraFollow is an unreferenced engine sample → trimmed.
        GameExporter.GeneratePlayerProject(
            input: Input(dir),
            sdkRoot: dir,
            playerDir: Path.Combine(path1: dir, path2: "player"),
            exeName: "MyGame",
            sceneScriptClasses: new HashSet<string> { "Samples.Scripting.Rotator" }
        );

        string reg = File.ReadAllText(
            Path.Combine(path1: dir, path2: "player", path3: "ScriptRegistration.g.cs")
        );
        Assert.Contains(
            expectedSubstring: "r.Register(typeof(global::Samples.Scripting.Rotator));",
            actualString: reg
        );
        Assert.DoesNotContain(expectedSubstring: "CameraFollow", actualString: reg);
    }

    [Fact]
    public void CollectScriptClasses_WalksTheTree()
    {
        var scene = new SceneGraph();
        var child = new SceneNode(name: "Car", kind: NodeKind.Mesh) {
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
        Assert.Equal(
            expected: ["Game.CarController", "Game.ChaseCamera"],
            actual: classes.OrderBy(c => c)
        );
    }

    [Fact]
    public void GeneratedCsproj_WiresPlayerAndScripts()
    {
        string dir = TempDir();
        GameExporter.GeneratePlayerProject(
            input: Input(
                dir: dir,
                scriptProject: "scripts/Game.Scripts.csproj",
                scriptAsm: "Game.Scripts"
            ),
            sdkRoot: dir,
            playerDir: Path.Combine(path1: dir, path2: "player"),
            exeName: "MyGame"
        );

        string csproj =
            File.ReadAllText(Path.Combine(path1: dir, path2: "player", path3: "Game.csproj"));
        Assert.Contains(
            expectedSubstring: "<AssemblyName>MyGame</AssemblyName>",
            actualString: csproj
        );
        Assert.Contains(expectedSubstring: "Zigote.Player.csproj", actualString: csproj);
        Assert.Contains(expectedSubstring: "Game.Scripts.csproj", actualString: csproj);
        Assert.Contains(
            expectedSubstring: """<TrimmerRootAssembly Include="Game.Scripts" />""",
            actualString: csproj
        );
        Assert.Contains(
            expectedSubstring: """<TrimmerRootAssembly Include="Zigote.Runtime" />""",
            actualString: csproj
        );
        Assert.Contains(expectedSubstring: "<PublishAot>true</PublishAot>", actualString: csproj);
    }

    [Fact]
    public void GeneratedCsproj_NoScriptProject_OmitsReference()
    {
        string dir = TempDir();
        GameExporter.GeneratePlayerProject(
            input: Input(dir),
            sdkRoot: dir,
            playerDir: Path.Combine(path1: dir, path2: "player"),
            exeName: "MyGame"
        );

        string csproj =
            File.ReadAllText(Path.Combine(path1: dir, path2: "player", path3: "Game.csproj"));
        Assert.DoesNotContain(expectedSubstring: "Scripts.csproj", actualString: csproj);
        Assert.Contains(expectedSubstring: "Zigote.Player.csproj", actualString: csproj);
    }

    [Fact]
    public void BakeVfxGraphs_BakesEmitterAndClearsGraph()
    {
        string dir = TempDir();
        var scene = new SceneGraph();
        scene.Root.Children.Add(
            new SceneNode(name: "Sparks", kind: NodeKind.VfxEmitter) { Parent = scene.Root }
        );
        string path = Path.Combine(path1: dir, path2: "main.scene");
        scene.Save(path);

        GameExporter.BakeVfxGraphs(scenePath: path, log: new NullProgress());

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
        string dir = TempDir();
        string src = Path.Combine(path1: dir, path2: "blob.zmesh");
        byte[] payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);
        // Mix in compressible structure so the codec has something to chew on.
        Array.Fill(
            array: payload,
            value: (byte)7,
            startIndex: 0,
            count: 16 * 1024
        );
        File.WriteAllBytes(path: src, bytes: payload);

        string shipped = Path.Combine(path1: dir, path2: "shipped", path3: "blob.zmesh");
        Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
        long compressedSize = ContentFiles.WriteCompressed(src: src, dst: shipped);

        Assert.False(File.Exists(shipped)); // only the .zst variant ships
        Assert.True(File.Exists(shipped + ".zst"));
        Assert.True(compressedSize > 0 && compressedSize < payload.Length);

        Assert.True(ContentFiles.Exists(shipped));
        Assert.Equal(expected: payload, actual: ContentFiles.ReadAllBytes(shipped));

        // Plain files still read as-is (the editor's loose-file path).
        Assert.Equal(expected: payload, actual: ContentFiles.ReadAllBytes(src));
    }

    [Fact]
    public void BakeVfxGraphs_NoEmitters_LeavesSceneUntouched()
    {
        string dir = TempDir();
        var scene = new SceneGraph();
        scene.Root.Children.Add(
            new SceneNode(name: "Cube", kind: NodeKind.Mesh) { Parent = scene.Root }
        );
        string path = Path.Combine(path1: dir, path2: "main.scene");
        scene.Save(path);
        byte[] before = File.ReadAllBytes(path);

        GameExporter.BakeVfxGraphs(scenePath: path, log: new NullProgress());

        Assert.Equal(expected: before, actual: File.ReadAllBytes(path));
    }

    private sealed class NullProgress : IProgress<string>
    {
        public void Report(string value) { }
    }
}
