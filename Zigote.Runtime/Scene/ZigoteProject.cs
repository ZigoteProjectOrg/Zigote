using System.Text.Json;
using Zigote.Core.Native;
using Zigote.Runtime.Serialization;

namespace Zigote.Runtime.Scene;

public class ZigoteProject
{
    // IncludeFields: ZgRenderSettings3D is a native-interop struct of public *fields*, which the
    // default serializer skips. WriteIndented keeps the .zigoteproj human-readable/diffable.
    // The source-gen resolver makes manifest load work under NativeAOT.
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        IncludeFields = true,
        TypeInfoResolver = RuntimeJsonContext.Default,
    };

    public string Name { get; set; } = "New Project";
    public string AssetRoot { get; set; } = "assets";
    public string StartupScene { get; set; } = "assets/main.scene";

    /// <summary>
    ///     Optional path to a user script .csproj, relative to this project file.
    ///     The editor will build it on startup and watch for changes.
    /// </summary>
    public string? ScriptProject { get; set; }

    /// <summary>
    ///     Persisted 3D render settings (environment, post-processing, shadows, material, …) — applied
    ///     to the engine on open, captured on save. Debug-only fields (diagnostic mode, debug view,
    ///     wireframe) are deliberately NOT persisted (always loaded/saved as off). Null on a project
    ///     that has never saved settings, in which case the engine's built-in defaults apply.
    /// </summary>
    public ZgRenderSettings3D? RenderSettings { get; set; }

    /// <summary>Standalone player window size. Ignored by the editor (it sizes its own window).</summary>
    public int WindowWidth { get; set; } = 1280;

    public int WindowHeight { get; set; } = 720;

    /// <summary>
    ///     Ship the DevTools overlay (Shift+D) with exported games. Off by default so a release
    ///     never carries the debug HUD; when enabled the exporter bundles the Zigote.UI.DevTools
    ///     assemblies and the standalone player installs the overlay at startup. Ignored by the
    ///     editor (it always installs its own DevTools).
    /// </summary>
    public bool DevToolsEnabled { get; set; }

    public static ZigoteProject Load(string path)
    {
        if (!File.Exists(path)) return new ZigoteProject();
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<ZigoteProject>(json, JsonOptions) ??
                      new ZigoteProject();
        // Manifests authored on Windows may carry backslashes; keep the canonical separator so the
        // same project file opens everywhere.
        project.AssetRoot = project.AssetRoot.Replace('\\', '/');
        project.StartupScene = project.StartupScene.Replace('\\', '/');
        project.ScriptProject = project.ScriptProject?.Replace('\\', '/');
        return project;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    ///     Create a fresh project on disk under <paramref name="projectDir" />: a
    ///     &lt;name&gt;.zigoteproj file, an assets/ folder, and a starter scene.
    ///     Returns the absolute path to the new .zigoteproj file.
    /// </summary>
    public static string Scaffold(string projectDir, string name)
    {
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "assets"));

        var project = new ZigoteProject {
            Name = name,
            AssetRoot = "assets",
            StartupScene = "assets/main.scene",
        };

        var projPath = Path.Combine(projectDir, name + ".zigoteproj");
        project.Save(projPath);
        SceneGraph.Demo().Save(Path.Combine(projectDir, project.StartupScene));
        return Path.GetFullPath(projPath);
    }
}
