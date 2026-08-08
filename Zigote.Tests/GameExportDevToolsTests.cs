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
        var dir = Path.Combine(
            Path.GetTempPath(),
            "zigote-export-tests",
            Guid.NewGuid().ToString("N")
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
        var projPath = Path.Combine(dir, "game.zigoteproj");
        project.Save(projPath);
        return new ExportInput(
            projPath,
            project,
            new ScriptRegistry(),
            null
        );
    }

    [Fact]
    public void GeneratedCsproj_DevToolsDisabled_OmitsDevTools()
    {
        var dir = TempDir();
        GameExporter.GeneratePlayerProject(
            Input(dir, false),
            dir,
            Path.Combine(dir, "player"),
            "MyGame"
        );

        var csproj = File.ReadAllText(Path.Combine(dir, "player", "Game.csproj"));
        Assert.DoesNotContain("Zigote.UI.DevTools", csproj);
    }

    [Fact]
    public void GeneratedCsproj_DevToolsEnabled_BundlesDevTools()
    {
        var dir = TempDir();
        GameExporter.GeneratePlayerProject(
            Input(dir, true),
            dir,
            Path.Combine(dir, "player"),
            "MyGame"
        );

        var csproj = File.ReadAllText(Path.Combine(dir, "player", "Game.csproj"));
        Assert.Contains("Zigote.UI.DevTools.csproj", csproj);
        Assert.Contains("""<TrimmerRootAssembly Include="Zigote.UI.DevTools" />""", csproj);
    }

    [Fact]
    public void ZigoteProject_DevToolsEnabled_RoundTripsAndDefaultsOff()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "game.zigoteproj");
        new ZigoteProject { DevToolsEnabled = true }.Save(path);
        Assert.True(ZigoteProject.Load(path).DevToolsEnabled);

        // Manifests written before the flag existed load with the overlay off.
        new ZigoteProject().Save(path);
        Assert.False(ZigoteProject.Load(path).DevToolsEnabled);
    }
}