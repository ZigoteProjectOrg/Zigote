using Zigote.Core.Engine;
using Zigote.Runtime.Content;
using Zigote.Runtime.Scene;
using Zigote.Scripting.Metadata;

namespace Zigote.Runtime;

/// <summary>
///     Editor-free game host: loads a <c>.zigoteproj</c> + its startup scene and runs the play loop
///     (<see cref="GameSession" />) the way the editor's play mode does, minus selection/undo/panels.
///     The standalone player builds on this; the editor keeps its own <c>EditorState</c> wrapper.
/// </summary>
public sealed class GameHost : IDisposable
{
    // Same hitch clamp as the editor's TickPlay: a slow frame (or the first after resume) must not
    // spike the fixed-step solver backlog.
    private const float MaxStep = 1f / 30f;

    private GameHost(ZigoteProject project, string projectDir, SceneGraph scene)
    {
        Project = project;
        ProjectDir = projectDir;
        Scene = scene;
    }

    public ZigoteProject Project { get; }
    public string ProjectDir { get; }
    public SceneGraph Scene { get; }
    public ScriptRegistry Scripts { get; } = new();
    public GameSession? Session { get; private set; }

    /// <summary>The host-owned 2D sprite renderer (textures/shaders cached across the session).</summary>
    public Sprite2DSystem Sprites2D { get; } = new();

    public void Dispose()
    {
        if (Session is not null)
        {
            Session.Restore(Scene.Root);
            Session = null;
        }

        Sprites2D.Dispose();
    }

    /// <summary>
    ///     Load a project file and its startup scene. Sets the process cwd to the project dir so
    ///     every scene-relative asset path (.zmesh blobs, textures, HDRI) resolves as in the editor.
    /// </summary>
    public static GameHost Load(string projectFile)
    {
        projectFile = Path.GetFullPath(projectFile);
        var project = ZigoteProject.Load(projectFile);
        string projDir = Path.GetDirectoryName(projectFile) ?? ".";
        Directory.SetCurrentDirectory(projDir);

        if (!File.Exists(project.StartupScene))
        {
            throw new FileNotFoundException(
                message: $"Startup scene not found: {project.StartupScene}",
                fileName: project.StartupScene
            );
        }

        return new GameHost(
            project: project,
            projectDir: projDir,
            scene: SceneGraph.Load(project.StartupScene)
        );
    }

    /// <summary>Apply the project's saved render settings (or engine defaults) with debug views stripped.</summary>
    public void ApplyRenderSettings()
    {
        var s = Project.RenderSettings ?? RenderDefaults.Settings3D();
        s.DiagnosticMode = 0f;
        s.DebugView = 0f;
        s.Wireframe = 0f;
        ZigoteEngine.Instance?.SetRenderSettings3D(s);
    }

    /// <summary>Load the scene's HDRI environment (cwd-relative), falling back to the procedural sky.</summary>
    public void ApplyEnvironment()
    {
        if (ZigoteEngine.Instance is not { } engine) return;
        if (!string.IsNullOrEmpty(Scene.EnvironmentPath) &&
            ContentFiles.Exists(Scene.EnvironmentPath))
            engine.SetEnvironmentHdri(ContentFiles.ReadAllBytes(Scene.EnvironmentPath));
        else
        {
            if (!string.IsNullOrEmpty(Scene.EnvironmentPath))
            {
                Console.Error.WriteLine(
                    $"[Zigote] environment '{Scene.EnvironmentPath}' not found; using procedural."
                );
            }

            engine.SetEnvironmentProcedural();
        }
    }

    /// <summary>Start the game session. Scripts must already be registered on <see cref="Scripts" />.</summary>
    public void Start()
    {
        if (Session is not null) return;
        Session = new GameSession(
            root: Scene.Root,
            registry: Scripts,
            sprites: Sprites2D,
            host: new GameSessionHostInfo {
                ScenePath = Project.StartupScene,
                SaveDirectory = Path.Combine(
                    path1: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    path2: string.IsNullOrWhiteSpace(Project.Name) ? "ZigoteGame" : Project.Name,
                    path3: "saves"
                ),
            }
        );
        // First play frame must use the scene's Camera node state, not whatever the engine last saw.
        Scene.Root.SyncToNativeBatched();
    }

    /// <summary>Advance one frame. Call after the UI frame, like the editor's TickPlay.</summary>
    public void Tick(float dt)
    {
        if (Session is null) return;
        if (dt > MaxStep) dt = MaxStep;
        Session.Update(root: Scene.Root, dt: dt);
    }
}
