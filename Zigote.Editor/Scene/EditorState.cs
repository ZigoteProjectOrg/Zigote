using System.Collections.Concurrent;
using Zigote.Core;
using Zigote.Core.Assets;
using Zigote.Core.Engine;
using Zigote.Core.Native;
using Zigote.Core.State;
using Zigote.Core.Threading;
using Zigote.Editor.Assets;
using Zigote.Editor.History;
using Zigote.Editor.Prefab;
using Zigote.Editor.Settings;
using Zigote.Runtime.Animation;
using Zigote.Runtime.Scene;
using Zigote.Scripting;
using Zigote.Scripting.Compilation;
using Zigote.Scripting.Loading;
using Zigote.Scripting.Metadata;
using Zigote.UI.Host;

namespace Zigote.Editor.Scene;

/// <summary>Single source of truth shared by all editor panels.</summary>
public sealed class EditorState : IDisposable
{
    // Largest physics/script step fed in play mode. A frame hitch (GC, script recompile, window drag,
    // resume after a pause) makes the unclamped wall-clock dt huge; passing that straight into
    // PhysicsWorld.Step tunnels fast bodies through geometry or explodes the solver. Clamp so a long
    // frame degrades into momentary slow-motion instead of an unstable jump (~30 fps floor).
    private const float MaxPlayStep = 1f / 30f;
    private readonly List<AnimationClip> _animClips = [];

    // ── Rename-aware assets ────────────────────────────────────────────────────

    // FileSystemWatcher renames arrive on a watcher thread; healing touches scene path setters (which
    // push to native for live nodes), so queue and apply on the main thread in PumpAssets.
    private readonly ConcurrentQueue<(string OldRel, string NewRel)>
        _pendingAssetRenames = new();

    // Editor selection captured on StartPlay and restored on StopPlay (play mode starts deselected).
    private readonly List<SceneNode> _savedPlaySelection = [];
    private readonly HashSet<SceneNode> _selectedNodes = [];

    /// <summary>
    ///     The authored render settings — project defaults + Settings-panel edits — independent of the
    ///     edit-mode reduced-graphics preset. <see cref="PushEffective3D" /> derives what the engine runs
    ///     from this, so toggling the preset (or entering play, which always runs full quality) never
    ///     loses what the user authored.
    /// </summary>
    public ZgRenderSettings3D Authored3D;

    private bool _reducedEditorGraphics;

    // Reactive primary selection — panels subscribe to SelectionSignal instead of an event.
    private SceneNode? _savedPlayPrimary;
    private FileSystemWatcher? _scriptWatcher;
    private string? _watchedProjectPath;
    private Timer? _watcherDebounce;

    public EditorState()
    {
        // Every scene/asset mutation invalidates the viewport's cached 3D frame (the viewport gates the
        // native re-render on this version — see ViewportPanel.Paint). Subscribing here covers every
        // fire site of both events by construction.
        SceneChanged += () => ViewportVersion++;
        AssetsChanged += () => ViewportVersion++;

        // Reads Assets/ProjectDir through delegates so it always sees the current registry (reassigned
        // by LoadAssets) and the open project's root.
        Prefabs = new PrefabService(assets: () => Assets, projectDir: () => ProjectDir);

        // Streaming: resolve an AssetId back to an absolute .zmesh path; the mesh sink registers a node's
        // mesh path to an AssetId and uploads the loaded blob (main thread) / hides on unload. Off by
        // default (StreamDistance == 0), so the synchronous mesh path is unchanged until streaming is enabled.
        AssetLoader = new AssetManager(id =>
            {
                string? rel = Assets.Resolve(id);
                return rel is null
                    ? null
                    : AssetPath.ToAbsolute(relativePath: rel, contentRoot: ProjectDir);
            }
        );
        MeshStreamer = new MeshStreamer(
            assets: AssetLoader,
            resolve: node => node.MeshPath is null
                ? AssetId.Empty
                : Assets.Register(
                    AssetPath.ToRelative(path: node.MeshPath, contentRoot: ProjectDir)
                ),
            upload: (node, bytes) =>
            {
                if (node.Handle != 0)
                    ZigoteEngine.Instance?.SceneSetMeshBlob(nodeHandle: node.Handle, data: bytes);
            },
            unload: node =>
            {
                if (node.Handle != 0)
                {
                    ZigoteEngine.Instance?.SceneSetNodeVisible(
                        nodeHandle: node.Handle,
                        visible: false
                    );
                }
            }
        );

        // Pre-load built-in sample scripts so they're discoverable without an external build step.
        ScriptRegistry.Load(typeof(Component).Assembly);

        // Initial sync of demo scene
        NotifySceneChanged();
    }

    /// <summary>
    ///     Where the editor's off-thread work runs, and the frame budget deferred UI work spends.
    ///     Panels take a <see cref="Zigote.Core.Threading.Background.Child" /> of it, so a failed
    ///     project scan is reported as <c>app/assets</c> and does not stop anything else. The host
    ///     wires the UI-thread hop and the per-frame drain (see <c>Program</c>).
    /// </summary>
    public Background Background { get; } = new(
        toUi: action => App.Active?.Post(action),
        requestFrame: () => App.Active?.RequestLayout()
    );

    public CommandHistory History { get; } = new();
    public SceneGraph Scene { get; private set; } = SceneGraph.Demo();
    public AssetRegistry Assets { get; private set; } = new();

    /// <summary>
    ///     Prefab asset operations (create/instantiate). Reads <see cref="Assets" />/
    ///     <see cref="ProjectDir" /> live.
    /// </summary>
    public PrefabService Prefabs { get; }

    /// <summary>
    ///     Background streaming asset cache (off-thread <c>.zmesh</c> reads → main-thread pump).
    ///     Ticked each frame.
    /// </summary>
    public AssetManager AssetLoader { get; }

    /// <summary>
    ///     Demand-driven mesh residency sink (distance load/unload); active only when
    ///     <see cref="StreamDistance" /> &gt;
    ///     0.
    /// </summary>
    public MeshStreamer MeshStreamer { get; }

    /// <summary>
    ///     Stream meshes in within this distance and unload beyond it (0 = off: all meshes stay
    ///     resident).
    /// </summary>
    public float StreamDistance { get; set; }

    /// <summary>Whether demand mesh streaming is enabled (a non-zero <see cref="StreamDistance" />).</summary>
    public bool StreamingEnabled => StreamDistance > 0f;

    /// <summary>Primary (gizmo/inspector) selection. Always a member of SelectedNodes when non-null.</summary>
    public SceneNode? Selected => SelectionSignal.Value;

    /// <summary>
    ///     Reactive signal for the primary selection — subscribe to receive the new value on every
    ///     change.
    /// </summary>
    public Signal<SceneNode?> SelectionSignal { get; } = new(null);

    /// <summary>All currently selected nodes. Single-select = exactly one entry.</summary>
    public IReadOnlySet<SceneNode> SelectedNodes => _selectedNodes;

    public string AssetRoot { get; set; } = "assets/";
    public string ScenePath { get; set; } = "assets/main.scene";

    /// <summary>Absolute path to the open .zigoteproj file, or null for an unsaved/scratch session.</summary>
    public string? ProjectPath { get; set; }

    /// <summary>
    ///     Absolute path to the open project's root directory. Project-relative paths resolve from
    ///     here.
    /// </summary>
    public string? ProjectDir { get; set; }

    /// <summary>
    ///     The loaded project descriptor (.zigoteproj), or null for a scratch session. Holds the
    ///     persisted render settings; updated and re-saved by <see cref="SaveProjectSettings" />.
    /// </summary>
    public ZigoteProject? Project { get; set; }

    /// <summary>
    ///     Per-project editor preferences (viewport toggles, dock layout) over the project-relative
    ///     prefs file — set on open, disposed (and thereby flushed) with the session. Null for a
    ///     scratch session: writers fall back to the plain session flags.
    /// </summary>
    public ProjectPreferences? Preferences { get; set; }

    // Asset browser
    public string AssetFilter { get; set; } = "";

    // Snap-to-grid (0 = off)
    public float SnapGrid { get; set; } = 0f;

    /// <summary>
    ///     Draw physics collision shapes as a wireframe overlay in the viewport (edit + play mode).
    ///     Toggled from the debug menu's "render.physics_wireframe" variable; read by ViewportPanel.
    /// </summary>
    public bool ShowPhysicsWireframe { get; set; }

    /// <summary>
    ///     Render VFX particles through the native wgpu billboard pass (additive/alpha GPU blend) instead
    ///     of the editor's 2D-projection overlay. Off by default — toggled from "render.vfx_native".
    /// </summary>
    public bool UseNativeVfx { get; set; }

    /// <summary>
    ///     Simulate VFX emitters on the GPU (compute kernel) instead of the CPU + native-upload path —
    ///     the scale path. Implies the native renderer. Off by default — toggled from "render.vfx_gpu".
    /// </summary>
    public bool UseGpuVfx { get; set; }

    /// <summary>
    ///     Continuously animate VFX emitters in edit mode (forces continuous rendering). Off by default —
    ///     a static, representative first-frame preview is always shown for emitters instead, with no
    ///     continuous render. Toggled from "render.vfx_edit".
    /// </summary>
    public bool AnimateEditVfx { get; set; }

    // Script system
    public ScriptDomain ScriptDomain { get; } = new();
    public ScriptRegistry ScriptRegistry { get; } = new();

    // Script build status
    public bool IsScriptBuilding { get; private set; }
    public IReadOnlyList<ScriptDiagnostic> ScriptDiagnostics { get; private set; } = [];

    /// <summary>
    ///     Monotonic version of the 3D viewport's inputs. Bumped by every <see cref="SceneChanged" /> /
    ///     <see cref="AssetsChanged" /> fire (subscribed in the ctor) and by
    ///     <see cref="InvalidateViewport" />
    ///     for render-affecting state that flows outside those events (HDRI swaps, debug render toggles).
    ///     The viewport folds it into the signature that gates the native 3D re-render.
    /// </summary>
    public int ViewportVersion { get; private set; }

    // Play state
    public bool IsPlaying { get; private set; }

    /// <summary>
    ///     Play mode is running but its simulation (scripts + physics) is frozen. Only meaningful
    ///     while <see cref="IsPlaying" />; the viewport keeps rendering the last frame.
    /// </summary>
    public bool IsPaused { get; private set; }

    public GameSession? ActivePlay { get; private set; }

    /// <summary>
    ///     The host-owned 2D sprite renderer: the viewport drives it every frame (edit AND play), the
    ///     play session wires the Sprites provider over it. Texture/shader caches are keyed on absolute
    ///     paths and destroyed with this state (scene close / project switch), not on play stop.
    /// </summary>
    public Sprite2DSystem Sprites2D { get; } = new();

    /// <summary>Animation playback (Task 3). Plays imported glTF clips; ticked from the main loop.</summary>
    public AnimationPlayer AnimationPlayer { get; } = new();

    /// <summary>All animation clips found in the current scene (for the timeline clip selector).</summary>
    public IReadOnlyList<AnimationClip> AnimationClips => _animClips;

    // ── Asset registry ────────────────────────────────────────────────────────

    private string RegistryPath => Path.Combine(path1: ProjectDir ?? ".", path2: "assets.registry");

    /// <summary>
    ///     Edit-mode reduced graphics: render the viewport without TAA / bloom / SSR / DoF while
    ///     authoring. Play mode always runs the authored (full) settings. Off by default — the viewport
    ///     is WYSIWYG unless the user opts in (Settings panel / `render.editor_reduced`).
    /// </summary>
    public bool ReducedEditorGraphics
    {
        get => _reducedEditorGraphics;
        set
        {
            if (_reducedEditorGraphics == value) return;
            _reducedEditorGraphics = value;
            PushEffective3D();
            InvalidateViewport();
            EditorGraphicsChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        // Tear the play session down FIRST (while the user component types are still loaded): this runs
        // every script's OnDisable/OnDestroy and disposes the native Jolt world + unwires the static
        // providers. Without this, quitting / closing / switching projects mid-play leaks the Jolt world
        // for the engine's lifetime and strands Physics/Input/Instancing on a dead session. Must precede
        // ScriptDomain.Dispose() (which unloads the collectible AssemblyLoadContext).
        // Before anything it might deliver into: a scan landing on a torn-down project is the one
        // failure mode a background result has, and this is the moment it would happen.
        Background.Dispose();
        StopPlay();
        StopScriptWatcher();
        Sprites2D.Dispose(); // destroy cached sprite textures/shaders before the project goes away

        // Release this project's GPU scene — meshes, geometry, material textures, the scene graph — and
        // the reflection probe, so closing a project to the welcome screen (no subsequent LoadScene to
        // clear it) or switching projects leaves no previous-project GPU resources resident.
        if (ZigoteEngine.Instance is { } engine)
        {
            engine.SceneClear();
            engine.ClearReflectionProbe();
        }

        ScriptDomain.Dispose();

        Preferences?.Dispose(); // flushes the project-relative prefs file
        Preferences = null;
    }

    /// <summary>Force the viewport to re-render the 3D scene on its next paint.</summary>
    public void InvalidateViewport() => ViewportVersion++;

    /// <summary>Load the per-project asset GUID registry from disk (call on project open).</summary>
    public void LoadAssets() => Assets = AssetRegistry.Load(RegistryPath);

    /// <summary>Persist the asset GUID registry to disk (call alongside scene save).</summary>
    public void SaveAssets()
    {
        if (ProjectDir is null) return;
        Assets.Save(RegistryPath);
    }

    // ── Project render settings ──────────────────────────────────────────────

    // Debug-only render fields are session state, never persisted to the project — forced off when
    // settings are saved or applied. Keep in sync with the "Diagnostics" group in SettingsPanel.
    private static void StripDebug(ref ZgRenderSettings3D s)
    {
        s.DiagnosticMode = 0f;
        s.DebugView = 0f;
        s.Wireframe = 0f;
    }

    /// <summary>Fired when <see cref="ReducedEditorGraphics" /> changes (persistence hook).</summary>
    public event Action? EditorGraphicsChanged;

    /// <summary>
    ///     Update the authored settings (Settings-panel writes) and push the effective ones. The panel
    ///     writes the whole struct — including its own Diagnostics rows — so this deliberately does NOT
    ///     preserve the engine's transient debug state (pre-existing panel semantics).
    /// </summary>
    public void Apply3D(in ZgRenderSettings3D s)
    {
        Authored3D = s;
        PushEffective3D(false);
    }

    /// <summary>
    ///     Push <see cref="Authored3D" /> to the engine, minus the heavy post passes (TAA, bloom, SSR,
    ///     DoF) when the edit-mode reduced-graphics preset is active. Call after anything that changes
    ///     which variant should be live (authored edits, preset toggle, play start/stop).
    ///     <paramref name="preserveDebugState" /> carries the engine's live wireframe/debug-view/
    ///     diagnostic fields over (the debug-menu Renderer panel writes those straight to the engine and
    ///     they never reach <see cref="Authored3D" /> — an implicit push like a play transition must not
    ///     silently revert them); pass false when the caller owns them (project open resets, panel
    ///     writes).
    /// </summary>
    public void PushEffective3D(bool preserveDebugState = true)
    {
        var s = Authored3D;
        if (!IsPlaying && _reducedEditorGraphics)
        {
            s.TaaEnabled = 0f;
            s.BloomIntensity = 0f;
            s.SsrIntensity = 0f;
            s.DofEnabled = 0f;
        }

        try
        {
            if (ZigoteEngine.Instance is not { } engine) return;
            if (preserveDebugState)
            {
                var cur = engine.GetRenderSettings3D();
                s.DiagnosticMode = cur.DiagnosticMode;
                s.DebugView = cur.DebugView;
                s.Wireframe = cur.Wireframe;
            }

            engine.SetRenderSettings3D(s);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Editor] Failed to apply render settings: {ex.Message}");
        }
    }

    /// <summary>
    ///     Apply the open project's persisted render settings to the engine (call once the engine
    ///     is ready, on project open). A project that saved none gets the defaults — so opening it resets
    ///     the engine instead of inheriting the previously-open project's look (the engine is shared and
    ///     long-lived across project switches).
    /// </summary>
    public void ApplyProjectRenderSettings()
    {
        var s = Project?.RenderSettings ?? RenderDefaults.Settings3D();
        StripDebug(ref s);
        Authored3D = s;
        PushEffective3D(false);
    }

    /// <summary>
    ///     Capture the engine's current render settings (minus debug) into the open project and
    ///     write the .zigoteproj. Called alongside scene save.
    /// </summary>
    public void SaveProjectSettings()
    {
        if (Project is null || ProjectPath is null) return;
        try
        {
            if (ZigoteEngine.Instance is { } engine)
            {
                var s = engine.GetRenderSettings3D();
                StripDebug(ref s);
                if (!IsPlaying && ReducedEditorGraphics)
                {
                    // The engine currently holds the reduced edit-mode preset for these — persist the
                    // authored values instead so the preset never leaks into the project file.
                    s.TaaEnabled = Authored3D.TaaEnabled;
                    s.BloomIntensity = Authored3D.BloomIntensity;
                    s.SsrIntensity = Authored3D.SsrIntensity;
                    s.DofEnabled = Authored3D.DofEnabled;
                }

                Project.RenderSettings = s;
            }

            Project.Save(ProjectPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Editor] Failed to save project settings: {ex.Message}");
        }
    }

    // ── Script build & watch ─────────────────────────────────────────────────

    /// <summary>
    ///     Build the script project at <paramref name="projectPath" /> and hot-reload on success. The
    ///     build is incremental: if no source/project/dependency changed since the last success it is
    ///     skipped (see <see cref="ScriptCompiler" />). Pass <paramref name="force" /> to rebuild anyway.
    /// </summary>
    public async Task BuildScriptsAsync(string projectPath, bool force = false)
    {
        IsScriptBuilding = true;
        ScriptBuildStatusChanged?.Invoke();
        try
        {
            var result = await ScriptCompiler.BuildAsync(projectPath: projectPath, force: force);
            ScriptDiagnostics = result.Diagnostics;
            if (result.Success && result.OutputAssemblyPath != null)
            {
                // A cache hit means the assembly is byte-identical to what produced it. Only (re)load
                // when it isn't already the loaded one (initial open / project switch), so a spurious
                // file-watcher event that hashes the same doesn't needlessly churn the collectible ALC.
                if (result.Cached && ScriptDomain.IsLoaded &&
                    ScriptDomain.AssemblyPath == result.OutputAssemblyPath)
                    Console.WriteLine("[Zigote] Scripts unchanged — skipped rebuild");
                else
                    LoadScripts(result.OutputAssemblyPath);
            }
        }
        catch (Exception ex)
        {
            ScriptDiagnostics = [
                new ScriptDiagnostic {
                    Message = ex.Message,
                    Severity = DiagnosticSeverity.Error,
                },
            ];
        }
        finally
        {
            IsScriptBuilding = false;
            ScriptBuildStatusChanged?.Invoke();
        }
    }

    /// <summary>Watch <paramref name="projectPath" />'s directory for .cs changes and auto-rebuild.</summary>
    public void StartScriptWatcher(string projectPath)
    {
        StopScriptWatcher();
        string? dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (dir == null || !Directory.Exists(dir)) return;
        _watchedProjectPath = projectPath;
        _scriptWatcher = new FileSystemWatcher(path: dir, filter: "*.cs") {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _scriptWatcher.Changed += (_, _) => ScheduleWatcherBuild();
        _scriptWatcher.Created += (_, _) => ScheduleWatcherBuild();
        _scriptWatcher.Renamed += (_, _) => ScheduleWatcherBuild();
    }

    public void StopScriptWatcher()
    {
        _scriptWatcher?.Dispose();
        _scriptWatcher = null;
        _watcherDebounce?.Dispose();
        _watcherDebounce = null;
        _watchedProjectPath = null;
    }

    private void ScheduleWatcherBuild()
    {
        _watcherDebounce?.Dispose();
        _watcherDebounce = new Timer(
            callback: _ => { _ = BuildScriptsAsync(_watchedProjectPath!); },
            state: null,
            dueTime: 700,
            period: Timeout.Infinite
        );
    }

    /// <summary>
    ///     Load a compiled script assembly. Discovers component types and updates the registry.
    ///     Safe to call multiple times for hot reload.
    /// </summary>
    public void LoadScripts(string assemblyPath)
    {
        try
        {
            ScriptDomain.Load(assemblyPath);
            if (ScriptDomain.Assembly != null)
                ScriptRegistry.Load(ScriptDomain.Assembly);
            Console.WriteLine(
                $"[Zigote] Scripts loaded from {assemblyPath} — {ScriptRegistry.All.Count} component(s)"
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Zigote] Failed to load scripts: {ex.Message}");
        }
    }

    public void LoadScene(SceneGraph newScene)
    {
        // First clear native scene
        if (ZigoteEngine.Instance != null) ZigoteEngine.Instance.SceneClear();

        Scene = newScene;
        _selectedNodes.Clear();
        History.Clear();
        SelectionSignal.Set(null);

        LoadProfile.Reset();
        long syncT = LoadProfile.Mark();
        NotifySceneChanged();
        double syncMs = LoadProfile.Ms(LoadProfile.Since(syncT));

        long envT = LoadProfile.Mark();
        ApplySceneEnvironment();
        double envMs = LoadProfile.Ms(LoadProfile.Since(envT));

        if (LoadProfile.Enabled)
        {
            Console.Error.WriteLine(
                $"[LoadProfile] sync={syncMs:F0}ms (mesh {LoadProfile.Ms(LoadProfile.MeshTicks):F0}ms/" +
                $"{LoadProfile.MeshCount}n/{LoadProfile.MeshBytes / 1024}KB, normalMaps {LoadProfile.Ms(LoadProfile.NormalTicks):F0}ms/" +
                $"{LoadProfile.NormalCount}n, texBatch {LoadProfile.Ms(LoadProfile.TexBatchTicks):F0}ms) env={envMs:F0}ms"
            );
        }

        RefreshAnimations();
    }

    /// <summary>
    ///     Re-scan the scene for animation clips and select the first one for the timeline. Clips bind
    ///     to nodes by name (hierarchy-preserving import). Playback stays paused unless
    ///     <paramref name="autoPlay" /> — an explicit import previews immediately, but merely opening a
    ///     scene must not start a clip (a playing clip re-renders the viewport every frame forever).
    /// </summary>
    public void RefreshAnimations(bool autoPlay = false)
    {
        _animClips.Clear();
        CollectAnimations(node: Scene.Root, outClips: _animClips);
        AnimationPlayer.Clip = _animClips.Count > 0 ? _animClips[0] : null;
        if (autoPlay && AnimationPlayer.Clip is not null) AnimationPlayer.Play();
        SceneChanged?.Invoke();
    }

    /// <summary>Select which clip plays (timeline clip dropdown).</summary>
    public void SetActiveClip(int index)
    {
        if (index < 0 || index >= _animClips.Count) return;
        AnimationPlayer.Clip = _animClips[index];
        AnimationPlayer.Seek(0f);
    }

    /// <summary>Toggle play/pause on the active clip (timeline transport).</summary>
    public void ToggleAnimationPlay()
    {
        if (AnimationPlayer.Clip is null) return;
        if (AnimationPlayer.Playing) AnimationPlayer.Pause();
        else AnimationPlayer.Play();
    }

    /// <summary>Scrub to a time (seconds): pause, apply the pose, and push it to the renderer.</summary>
    public void SeekAnimation(float time)
    {
        if (AnimationPlayer.Clip is null) return;
        AnimationPlayer.Pause();
        AnimationPlayer.Seek(time);
        AnimationPlayer.ApplyTo(Scene.Root);
        NotifySceneChanged();
    }

    private static void CollectAnimations(SceneNode node, List<AnimationClip> outClips)
    {
        outClips.AddRange(node.Animations);
        foreach (var c in node.Children) CollectAnimations(node: c, outClips: outClips);
    }

    /// <summary>Advance + apply the active animation clip. Call each frame from the main loop.</summary>
    public void TickAnimation(float dt)
    {
        if (IsPlaying) return; // play-mode (physics/scripts) owns the scene
        if (AnimationPlayer.Clip is null || !AnimationPlayer.Playing) return;
        AnimationPlayer.Tick(dt);
        AnimationPlayer.ApplyTo(Scene.Root);
        NotifySceneChanged();
    }

    /// <summary>Apply the scene's environment map (HDRI) if set, else the procedural studio env.</summary>
    private void ApplySceneEnvironment()
    {
        if (ZigoteEngine.Instance is not { } engine) return;
        try
        {
            string? resolved = ResolveAssetPath(Scene.EnvironmentPath);
            if (resolved is not null)
            {
                engine.SetEnvironmentHdri(File.ReadAllBytes(resolved));
                Console.Error.WriteLine($"[Editor] environment loaded: {resolved}");
            }
            else
            {
                if (!string.IsNullOrEmpty(Scene.EnvironmentPath))
                {
                    Console.Error.WriteLine(
                        $"[Editor] environment '{Scene.EnvironmentPath}' not found; using procedural."
                    );
                }

                engine.SetEnvironmentProcedural();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Editor] environment load failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Resolve a scene-relative asset path against the cwd, the project dir, and the
    ///     repo's examples/ tree, so it loads whether launched from the project or the repo root.
    /// </summary>
    private string? ResolveAssetPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string[] candidates = [
            path,
            Path.Combine(path1: ProjectDir ?? ".", path2: path),
            Path.Combine(path1: "examples", path2: "PorscheDemo", path3: path),
        ];
        foreach (string c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        return null;
    }

    public void StartPlay()
    {
        if (IsPlaying) return;

        // Don't enter play mode while the project's scripts are still compiling: the component
        // registry isn't populated yet, so every scripted node would attach as "Unknown" and the
        // game logic (controllers, cameras, HUD) would silently not run. This is the open-project
        // race — BuildScriptsAsync runs in the background — so just wait for it to finish.
        if (IsScriptBuilding)
        {
            Console.Error.WriteLine(
                "[Zigote] Scripts are still building — Play is unavailable until the build finishes."
            );
            ScriptBuildStatusChanged?.Invoke();
            return;
        }

        try
        {
            var session = new GameSession(
                root: Scene.Root,
                registry: ScriptRegistry,
                sprites: Sprites2D,
                host: new GameSessionHostInfo {
                    ScenePath = ScenePath,
                    SaveDirectory = ProjectDir is { } saveRoot
                        ? Path.Combine(path1: saveRoot, path2: ".saves")
                        : null,
                }
            );
            // Push design-time camera position to Zig so the first play frame uses
            // the scene's Camera node, not the orbit editor camera position.
            Scene.Root.SyncToNativeBatched();
            IsPlaying = true;
            IsPaused = false;
            ActivePlay = session;
            PushEffective3D(); // play always runs the authored settings, even when edit mode is reduced

            // Capture + clear the editor selection so play mode starts clean (no gizmos / inspector
            // editing of the running scene); restored on StopPlay.
            _savedPlaySelection.Clear();
            _savedPlaySelection.AddRange(_selectedNodes);
            _savedPlayPrimary = Selected;
            Select(null);

            PlayStarted?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Zigote] GameSession failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Freeze the running simulation (scripts + physics) while keeping the session live. No-op
    ///     outside play mode.
    /// </summary>
    public void PausePlay()
    {
        if (!IsPlaying || IsPaused) return;
        IsPaused = true;
        PlayPausedChanged?.Invoke();
    }

    /// <summary>Resume a paused simulation. No-op if not playing or not paused.</summary>
    public void ResumePlay()
    {
        if (!IsPlaying || !IsPaused) return;
        IsPaused = false;
        PlayPausedChanged?.Invoke();
    }

    /// <summary>Toggle pause/resume. No-op outside play mode.</summary>
    public void TogglePause()
    {
        if (IsPaused) ResumePlay();
        else PausePlay();
    }

    public void StopPlay()
    {
        if (!IsPlaying) return;
        IsPlaying = false;
        IsPaused = false;
        ActivePlay?.Restore(Scene.Root);
        ActivePlay = null;

        // Restore the editor selection captured at play start (skip any node deleted during play).
        _selectedNodes.Clear();
        foreach (var n in _savedPlaySelection)
        {
            if (n.Parent != null)
                _selectedNodes.Add(n);
        }

        SelectionSignal.Set(_savedPlayPrimary is { Parent: not null } ? _savedPlayPrimary : null);
        _savedPlaySelection.Clear();
        _savedPlayPrimary = null;

        PushEffective3D(); // re-apply the edit-mode preset (after ActivePlay.Restore's own settings restore)
        NotifySceneChanged(); // re-push design-time transforms to Zig
    }

    /// <summary>
    ///     In play mode, push a changed exported field to the live component(s) on a node so inspector
    ///     edits take effect on the running script immediately (live tuning). No-op in edit mode — the
    ///     value is restored from <see cref="SceneNode.ScriptExports" /> when play next attaches.
    /// </summary>
    public void ApplyLiveScriptExport(SceneNode node, ExportedField field, string json)
    {
        if (!IsPlaying) return;
        ActivePlay?.ApplyExportedField(nodeId: node.Id, field: field, json: json);
    }

    /// <summary>Run one physics+camera tick. Call from the main loop after app.Frame().</summary>
    public void TickPlay(float dt)
    {
        if (!IsPlaying || IsPaused || ActivePlay is null) return;
        // Clamp the step so a frame hitch (or the first frame after resuming) can't spike the solver.
        if (dt > MaxPlayStep) dt = MaxPlayStep;
        // Tell game scripts whether anything will render their debug lines this frame, before they run.
        DebugDraw.Enabled = ShowPhysicsWireframe;
        ActivePlay.Update(root: Scene.Root, dt: dt);
        SceneChanged?.Invoke();
    }

    // Callbacks panels can subscribe to
    public event Action? SceneChanged;
    public event Action? AssetsChanged;
    public event Action<string, Offset>? AssetDropped;
    public event Action<string>? AssetSelected;
    public event Action<string>? OpenFileRequested;
    public event Action? PlayStarted;

    /// <summary>
    ///     Fired when the running play session is paused or resumed (drives the toolbar button +
    ///     overlay).
    /// </summary>
    public event Action? PlayPausedChanged;

    public event Action? ScriptBuildStatusChanged;

    /// <summary>Single-select. Clears the multi-selection, selects just this node.</summary>
    public void Select(SceneNode? node)
    {
        if (Selected == node && _selectedNodes.Count <= 1) return;
        _selectedNodes.Clear();
        if (node != null) _selectedNodes.Add(node);
        SelectionSignal.Set(node);
    }

    /// <summary>Ctrl+click: toggles <paramref name="node" /> in the selection set.</summary>
    public void AddToSelection(SceneNode node)
    {
        SceneNode? primary;
        if (!_selectedNodes.Remove(node))
        {
            _selectedNodes.Add(node);
            primary = node;
        }
        else
            primary = _selectedNodes.Count > 0 ? _selectedNodes.Last() : null;

        SelectionSignal.Set(primary);
    }

    /// <summary>Shift+click: replaces the selection with an explicit range of nodes.</summary>
    public void SetSelection(IEnumerable<SceneNode> nodes)
    {
        _selectedNodes.Clear();
        SceneNode? last = null;
        foreach (var n in nodes)
        {
            _selectedNodes.Add(n);
            last = n;
        }

        SelectionSignal.Set(last);
    }

    public void NotifySceneChanged()
    {
        // During play mode, GameSession owns the Zig scene; don't overwrite with design-time values.
        // Batched sync decodes any pending textures in parallel (cheap no-op once all are uploaded).
        if (!IsPlaying)
            Scene.Root.SyncToNativeBatched();
        SceneChanged?.Invoke();
    }

    public void NotifyAssetsChanged() => AssetsChanged?.Invoke();

    /// <summary>Report an on-disk rename observed by the asset browser's watcher (any-thread safe).</summary>
    public void QueueAssetRenamed(string oldFullPath, string newFullPath)
    {
        if (ProjectDir is not { } dir) return;
        _pendingAssetRenames.Enqueue(
            (AssetPath.ToRelative(path: oldFullPath, contentRoot: dir),
                AssetPath.ToRelative(path: newFullPath, contentRoot: dir))
        );
    }

    /// <summary>
    ///     Rename/move an asset file and heal every reference: the registry keeps its AssetId, and the
    ///     open scene's path references are rewritten in place. Main-thread only.
    /// </summary>
    public bool RenameAsset(string oldRelativePath, string newRelativePath)
    {
        if (ProjectDir is not { } dir) return false;
        string oldAbs = AssetPath.ToAbsolute(relativePath: oldRelativePath, contentRoot: dir);
        string newAbs = AssetPath.ToAbsolute(relativePath: newRelativePath, contentRoot: dir);
        if (!File.Exists(oldAbs) || File.Exists(newAbs)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newAbs)!);
            File.Move(sourceFileName: oldAbs, destFileName: newAbs);
        }
        catch (IOException ex)
        {
            EditorLog.Add(severity: LogSeverity.Warning, message: $"Rename failed: {ex.Message}");
            return false;
        }

        ApplyAssetRename(oldRel: oldRelativePath, newRel: newRelativePath);
        return true;
    }

    private void ApplyAssetRename(string oldRel, string newRel)
    {
        Assets.RenamePath(oldPath: oldRel, newPath: newRel);
        bool changed = AssetReferenceRewriter.RewriteScene(
            scene: Scene,
            oldRelativePath: oldRel,
            newRelativePath: newRel
        );
        SaveAssets();
        if (changed) NotifySceneChanged();
        NotifyAssetsChanged();
    }

    /// <summary>
    ///     Drain completed background asset loads on the main thread (the streaming pump). Call once
    ///     per frame. Returns true when a load landed — the host should request a paint, since the
    ///     event-driven editor won't otherwise show a streamed-in mesh until the next input.
    /// </summary>
    public bool PumpAssets(long frame)
    {
        // Heal watcher-observed renames first (main thread — scene rewrites push to native).
        bool healed = false;
        while (_pendingAssetRenames.TryDequeue(out var rename))
        {
            ApplyAssetRename(oldRel: rename.OldRel, newRel: rename.NewRel);
            healed = true;
        }

        if (AssetLoader.Pump(frame) == 0) return healed;
        InvalidateViewport();
        return true;
    }

    public void NotifyAssetDropped(string path, Offset screenPos) =>
        AssetDropped?.Invoke(arg1: path, arg2: screenPos);

    /// <summary>Raised when a file row is selected in the asset browser (drives the Asset preview panel).</summary>
    public void NotifyAssetSelected(string path) => AssetSelected?.Invoke(path);

    /// <summary>
    ///     Raised when a file should be opened in the code editor (double-click on a text/code
    ///     asset).
    /// </summary>
    public void NotifyOpenFile(string path) => OpenFileRequested?.Invoke(path);

    public void DeleteSelected()
    {
        // Only delete root nodes of the selection (don't double-delete children)
        var roots = _selectedNodes
            .Where(n => n.Parent != null && !_selectedNodes.Contains(n.Parent))
            .ToList();
        if (roots.Count == 0) return;
        foreach (var n in roots) History.Execute(new DeleteNodeCommand(state: this, node: n));
    }

    public SceneNode AddNode(string name, NodeKind kind)
    {
        var parent = Selected ?? Scene.Root;
        var node = new SceneNode(name: name, kind: kind);
        History.Execute(new AddNodeCommand(state: this, parent: parent, node: node));
        return node;
    }

    public void DuplicateSelected()
    {
        if (Selected is null || Selected.Parent is null) return;
        var copy = Selected.DeepClone(Selected.Name + " Copy");
        History.Execute(new AddNodeCommand(state: this, parent: Selected.Parent, node: copy));
    }

    /// <summary>
    ///     Save the selected node as a <c>.prefab</c> asset and link it as the first instance
    ///     (undoable).
    /// </summary>
    public AssetId CreatePrefabFromSelected()
    {
        if (Selected is null) return AssetId.Empty;
        var cmd = new CreatePrefabCommand(state: this, source: Selected);
        History.Execute(cmd);
        return cmd.PrefabId;
    }

    /// <summary>
    ///     Instantiate a prefab asset under <paramref name="parent" /> (or the selection / root).
    ///     Undoable.
    /// </summary>
    public SceneNode? InstantiatePrefab(AssetId prefab, SceneNode? parent = null)
    {
        if (prefab.IsEmpty) return null;
        var cmd = new InstantiatePrefabCommand(
            state: this,
            prefab: prefab,
            parent: parent ?? Selected ?? Scene.Root
        );
        History.Execute(cmd);
        return cmd.Node;
    }
}
