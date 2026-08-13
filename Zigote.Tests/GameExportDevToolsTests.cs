using Xunit;
using Zigote.Editor.Export;
using Zigote.Runtime.Scene;
using Zigote.Scripting.Metadata;

namespace Zigote.Tests;

/// <summary>
///     Pins the DevTools opt-in gate on export: the generated player csproj bundles the
///     Zigote.UI.DevTools assemblies only when the project manifest enables the overlay, so a
///     default export never ships the debug HUD.
/// </summary>
public class GameExportDevToolsTests
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

    private static ExportInput Input(string dir, bool devTools)
    {
        var project = new ZigoteProject {
            Name = "My Game",
            DevToolsEnabled = devTools,
        };
        string projPath = Path.Combine(path1: dir, path2: "game.zigoteproj");
        project.Save(projPath);
        return new ExportInput(
            ProjectPath: projPath,
            Project: project,
            Scripts: new ScriptRegistry(),
            ScriptAssemblyName: null
        );
    }

    [Fact]
    public void GeneratedCsproj_DevToolsDisabled_OmitsDevTools()
    {
        string dir = TempDir();
        GameExporter.GeneratePlayerProject(
            input: Input(dir: dir, devTools: false),
            sdkRoot: dir,
            playerDir: Path.Combine(path1: dir, path2: "player"),
            exeName: "MyGame"
        );

        string csproj =
            File.ReadAllText(Path.Combine(path1: dir, path2: "player", path3: "Game.csproj"));
        Assert.DoesNotContain(expectedSubstring: "Zigote.UI.DevTools", actualString: csproj);
    }

    [Fact]
    public void GeneratedCsproj_DevToolsEnabled_BundlesDevTools()
    {
        string dir = TempDir();
        GameExporter.GeneratePlayerProject(
            input: Input(dir: dir, devTools: true),
            sdkRoot: dir,
            playerDir: Path.Combine(path1: dir, path2: "player"),
            exeName: "MyGame"
        );

        string csproj =
            File.ReadAllText(Path.Combine(path1: dir, path2: "player", path3: "Game.csproj"));
        Assert.Contains(expectedSubstring: "Zigote.UI.DevTools.csproj", actualString: csproj);
        Assert.Contains(
            expectedSubstring: """<TrimmerRootAssembly Include="Zigote.UI.DevTools" />""",
            actualString: csproj
        );
    }

    [Fact]
    public void ZigoteProject_DevToolsEnabled_RoundTripsAndDefaultsOff()
    {
        string dir = TempDir();
        string path = Path.Combine(path1: dir, path2: "game.zigoteproj");
        new ZigoteProject { DevToolsEnabled = true }.Save(path);
        Assert.True(ZigoteProject.Load(path).DevToolsEnabled);

        // Manifests written before the flag existed load with the overlay off.
        new ZigoteProject().Save(path);
        Assert.False(ZigoteProject.Load(path).DevToolsEnabled);
    }
}
