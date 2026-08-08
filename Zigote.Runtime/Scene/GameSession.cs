using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Core.Physics;
using Zigote.Ecs;
using Zigote.Ecs.Prefab;
using Zigote.Ecs.Reflection;
using Zigote.Ecs.Scene;
using Zigote.Runtime.Vfx;
using Zigote.Scripting;
using Zigote.Scripting.Metadata;
using Zigote.Vfx;

namespace Zigote.Runtime.Scene;

/// <summary>Host-supplied context a play session needs but cannot derive from the scene tree.</summary>
public sealed record GameSessionHostInfo
{
    /// <summary>The scene file being played — seeds <c>Scenes.Current</c>.</summary>
    public string? ScenePath { get; init; }

    /// <summary>Base directory for the Save provider (per-project in the editor, app-data in the player).</summary>
    public string? SaveDirectory { get; init; }
}

/// <summary>
///     Manages a single play-mode session. Snapshots design-time transforms on construction, runs
///     scripts + physics each frame, and restores on stop. The session is game-agnostic: gameplay
///     (vehicles, cameras, etc.) lives in user <see cref="Component" /> scripts, not in here.
/// </summary>
public sealed class GameSession : IWorldSessionHooks
{
    private const float CameraSpeed = 5f; // m/s
    private const float LookSens = 0.003f; // rad / logical pixel

    // Camera projection is now per-camera: SceneNode.CameraFovDegrees/Near/Far (or the physical camera's
    // resolved values) drive both the native renderer (via SceneSetCameraParams in SyncToNative) and the
    // published culling frustum (PublishRenderView), so the two always agree. No hardcoded FOV here.

    // Fixed-timestep gameplay loop. Scripts + physics advance in constant FixedDt slices regardless of the
    // render frame rate, so simulation behaviour — a vehicle's top speed and handling above all — never
    // drifts with fps (the old path stepped both at the raw render dt). The render dt only decides HOW MANY
    // slices run this frame; time left over under one slice carries to the next, and the backlog is capped
    // (MaxCatchUp) so a long stall — alt-tab, a breakpoint — can't trigger a spiral of death.
    private const float FixedDt = 1f / 120f; // 120 Hz deterministic gameplay + physics tick

    private const float
        MaxCatchUp = 0.25f; // simulate at most 250 ms of backlog in a single render frame

    // Backs the generic Audio scripting API in play mode, and tracks the native source created for each
    // editor-authored AudioSource node (node id → source id) so they can be positioned + freed.
    private readonly RuntimeAudioBackend? _audio;

    /// <summary>
    ///     Live spatial sources by node id, carrying the node so the per-frame position update does not
    ///     have to find it again. Keeping the node here rather than re-walking the scene is what turns
    ///     that update from O(nodes) into O(sources); the reference lives exactly as long as the sound
    ///     handle beside it, since both are removed together in <see cref="ReleaseNodeResources" />.
    /// </summary>
    private readonly Dictionary<int, (SceneNode Node, uint Sound)> _audioSources = new();

    /// <summary>
    ///     Physics bodies by node id, carrying the node the transform is written back to. The node is
    ///     here for the same reason it is in <see cref="_audioSources" />: the read-back runs at the
    ///     fixed-step rate, and re-deriving this list by walking the scene made a 10k-node level cost a
    ///     full traversal per tick to find a few dozen dynamic bodies.
    /// </summary>
    private readonly Dictionary<int, (SceneNode Node, uint BodyId)> _bodyIds = new();

    // Backs the generic Cinematics scripting API: runtime control of the active camera's physical model.
    private readonly RuntimeCameraBackend? _cameraBackend;

    // Backs the Instancing provider in play mode (GPU-instanced mesh batches). Nullable: assigned during
    // construction, but Teardown() may run from the ctor's failure path before it is set.
    private readonly RuntimeInstancingBackend? _instancing;

    // Physical camera: per-frame resolve of the active camera's photographic model, applied to the global
    // render settings (and restored on disable/stop). Shared with the editor's edit-mode preview so both
    // modes show the same look.
    private readonly PhysicalCameraDriver _physDriver = new();

    // JoltPhysics world and per-node body ID map
    private readonly PhysicsWorld _physics = new();

    // Backs the generic Physics scripting API in play mode. Retained so the editor's physics-wireframe
    // overlay can enumerate script-created Jolt bodies (which are NOT node.UsePhysics SceneNodes).
    private readonly RuntimePhysicsBackend? _physicsBackend;

    // Design-time snapshots restored on Stop
    private readonly Dictionary<int, Vec3> _savedPos = new();
    private readonly Dictionary<int, Quat> _savedRot = new();
    private readonly Dictionary<int, Vec3> _savedScl = new();

    // Backs the generic Scenes scripting API (scene switch / additive load / fade), riding the World
    // backend's spawn machinery + scene-edit ledger.
    private readonly RuntimeScenesBackend? _scenes;

    // Script component instances
    private readonly ScriptWorld _scripts;

    // Reused flattened buffers for the batched physics→node transform sync (one FFI call per tick
    // instead of a position + rotation call pair per body; zero steady-state allocation).
    private readonly List<(SceneNode Node, uint BodyId)> _syncBodies = [];
    private ScratchBuffer<uint> _syncIds;
    private ScratchBuffer<float> _syncXforms;

    // The HOST-OWNED 2D sprite renderer (EditorState / GameHost); the session only wires the Sprites
    // scripting provider over it, advances sprite-node animation in the fixed loop, and resets the
    // session-scoped state on stop. Null when the host has no 2D system (headless tests).
    private readonly Sprite2DSystem? _sprites;

    // Editor-authored VfxEmitter nodes simulated on the CPU each fixed tick; the viewport draws the pools.
    private readonly VfxScenePlayback _vfx = new();

    // Backs the generic Vfx scripting API: script-spawned emitters, stepped + drawn alongside node ones.
    private readonly RuntimeVfxBackend _vfxBackend = new();

    // Backs the generic World scripting API (spawn/destroy/find/spatial queries over live entities).
    // Nullable: assigned during construction, but Teardown() may run from the ctor's failure path first.
    private readonly RuntimeWorldBackend? _world;
    private float _accumulator;

    private float _camPitch;

    // Play-mode camera orientation (accumulated from mouse-look)
    private float _camYaw;

    // flecs scene spine: every node gets an entity with a canonical Transform during play. Each frame the
    // settled SceneNode tree is baked into the entities (PushTransforms), an optional ECS-system pass can
    // mutate them, then PullTransforms mirrors the canonical entity Transforms back onto the nodes the
    // renderer reads — so flecs is the source of truth and the SceneNode is the render mirror. Nullable:
    // assigned in the ctor, and Teardown() may run from the ctor's failure path before it is set.
    public bool Handbrake;
    public float LookDx; // accumulated horizontal mouse delta this frame
    public float LookDy; // accumulated vertical mouse delta this frame
    public bool MoveBack;
    public bool MoveDown;

    // ── Input (written by ViewportPanel, read by Update) ──────────────────────

    /// <summary>
    ///     Every key currently held, by lower-case name ("a", "space", "enter", "left", …). The
    ///     named movement flags above cover the built-in drive controls; this is what lets a game
    ///     read ANY key — menus, a second couch player, custom bindings — through
    ///     <see cref="Input.IsKeyDown" /> without the host having to know the game's control scheme.
    /// </summary>
    public readonly HashSet<string> HeldKeys = [];

    /// <summary>Record a key transition. Hosts call this from their key handler.</summary>
    public void SetKey(string name, bool down)
    {
        if (string.IsNullOrEmpty(name)) return;
        var key = name.ToLowerInvariant();
        if (down) HeldKeys.Add(key);
        else HeldKeys.Remove(key);
    }

    public bool MoveForward;
    public bool MoveLeft;
    public bool MoveRight;
    public bool MoveUp;
    public bool ResetCar;

    // ── Construction ──────────────────────────────────────────────────────────

    public GameSession(SceneNode root, ScriptRegistry? registry = null,
        Sprite2DSystem? sprites = null,
        GameSessionHostInfo? host = null)
    {
        _sprites = sprites;
        _scripts = new ScriptWorld(registry ?? new ScriptRegistry());
        Time.Reset(); // each play session starts its clock at 0 — Elapsed must not carry across replays
        Snapshot(root);

        // Acquire native + global resources behind a guard: if any step throws (a bad PhysicsInit, or
        // — most likely — a user script throwing in OnCreate during Attach), Teardown() releases the
        // Jolt world and unwires every static provider before the exception propagates, so a failed
        // Play can never leak the native world or strand the engine on a dead session's closures.
        try
        {
            // Mirror the scene into a flecs world: entity-per-node + canonical Transform + ChildOf, seeded
            // from the authored transforms. No native engine needed (flecs is standalone), so it's safe
            // before PhysicsInit. Teardown() disposes it on any failure below.
            Ecs = new EcsSceneBridge();
            Ecs.BuildFrom(root);

            // Expose the live world to scripts (generic — entities/queries/prefabs) before OnCreate runs,
            // so a game component can spawn an ECS sub-simulation or instantiate a prefab in OnCreate.
            Scripting.Ecs.World = Ecs.World;
            Scripting.Ecs.Scene = Ecs;
            Scripting.Ecs.Prefabs = new EcsPrefabLibrary(Ecs.World, new EcsComponentRegistry());

            _physics.Initialize(ZigoteEngine.Instance!.Handle);
            RegisterBodies(root);
            _physics.OptimizeBroadPhase();

            // Expose the physics world to scripts (generic — raycast/forces/bodies) before OnCreate runs,
            // so a game component can create a rigid body in OnCreate.
            _physicsBackend = new RuntimePhysicsBackend(_physics);
            Physics.Backend = _physicsBackend;

            // Expose GPU instancing to scripts (generic — a component submits per-instance transforms).
            _instancing = new RuntimeInstancingBackend(root);
            Instancing.Backend = _instancing;

            // Expose spatial audio to scripts before OnCreate runs (a component can play a sound on create),
            // and spin up the native sources backing editor-authored AudioSource nodes.
            _audio = new RuntimeAudioBackend(ZigoteEngine.Instance!);
            Audio.Backend = _audio;
            InitAudioSources(root);

            // Expose runtime camera control (a CinematicCamera component / game script drives the active
            // camera's lens/film/focus). The per-frame resolve in PublishRenderView applies it.
            _cameraBackend = new RuntimeCameraBackend(root);
            Camera.Backend =
                _cameraBackend; // fully-qualified: the Camera provider lives in Zigote.Scripting

            // Build a CPU particle simulator per editor-authored VFX emitter (no native required), and
            // expose the generic Vfx API so a game component can spawn emitters in OnCreate.
            _vfx.Build(root);
            Scripting.Vfx.Backend =
                _vfxBackend; // fully-qualified: Zigote.Editor.Vfx namespace also in scope

            // Expose 2D sprites to scripts before OnCreate runs (a component can load textures /
            // compile sprite shaders there). Backed by the host's Sprite2DSystem so script textures
            // share its cache and script draws sort/batch with editor-authored Sprite nodes.
            if (_sprites != null) Sprites.Backend = new RuntimeSpritesBackend(_sprites);

            // Expose the runtime entity API (World.Spawn/Destroy/Find/spatial queries) before OnCreate
            // runs, so a component can spawn prefabs in OnCreate. The backend composes the script world,
            // the flecs bridge, and this session's per-subsystem resource hooks (physics/audio/VFX).
            _world = new RuntimeWorldBackend(
                root,
                _scripts,
                Ecs,
                this
            );
            Scripting.World.Backend =
                _world; // fully-qualified: the Zigote.World namespace also resolves here

            // Scene flow rides the World machinery (swap = destroy-all + graft; additive = graft),
            // so editor play-stop still restores the authored scene after a mid-play scene switch.
            _scenes = new RuntimeScenesBackend(_world, host?.ScenePath);
            Scenes.Backend = _scenes;

            // Persistence: publish the host's save directory so a game can build its own versioned
            // SaveStore (with migrations) in OnCreate. Save.Store itself is game-assigned.
            Scripting.Save.DefaultDirectory = host?.SaveDirectory;

            WireInputProviders(); // once — Update no longer reassigns these closures per frame

            _scripts.Attach(root);

            var cam = FindCamera(root);
            if (cam != null)
            {
                var e = cam.Rotation.ToEulerRadians();
                _camPitch = e.X;
                _camYaw = e.Y;
            }
        }
        catch
        {
            Teardown();
            throw;
        }
    }

    /// <summary>Script-created Jolt bodies (via the generic Physics API), for the wireframe overlay.</summary>
    internal IEnumerable<RuntimePhysicsBackend.DebugBody> ScriptBodies =>
        _physicsBackend?.DebugBodies() ?? [];

    /// <summary>Live VFX emitter simulations (node + CPU pool), for the viewport particle draw.</summary>
    internal IReadOnlyList<(SceneNode node, CpuParticleSimulator sim)> VfxEmitters => _vfx.Emitters;

    /// <summary>Per-node GPU-compute emitter drivers, for the native GPU particle path (render.vfx_gpu).</summary>
    internal IReadOnlyList<(SceneNode node, VfxGpuEmitter gpu)> GpuVfxEmitters => _vfx.GpuEmitters;

    /// <summary>
    ///     The flecs scene bridge for this play session (node↔entity map + canonical Transforms),
    ///     or null outside play. Lets the editor query the live entity set, instantiate prefabs, and read
    ///     per-entity components.
    /// </summary>
    public EcsSceneBridge? Ecs { get; private set; }

    /// <summary>Shorthand for the live flecs world during play (null outside play).</summary>
    public EcsWorld? EcsWorld => Ecs?.World;

    /// <summary>Black-overlay opacity of a scene-transition fade — host viewports draw it over the frame.</summary>
    public float ScreenFadeAlpha => _scenes?.FadeAlpha ?? 0f;

    /// <summary>
    ///     Every live particle simulation (editor-authored nodes + script-spawned emitters) with a stable
    ///     u64 batch key for the native billboard pass — node emitters key on the native handle; script
    ///     emitters on a high-bit-tagged id (cannot collide with a node pointer-handle).
    /// </summary>
    internal IEnumerable<(ulong key, CpuParticleSimulator sim)> AllVfxSimulators
    {
        get
        {
            foreach (var (node, sim) in _vfx.Emitters) yield return (node.Handle, sim);
            foreach (var (id, sim) in _vfxBackend.Emitters)
                yield return (0xF000_0000_0000_0000UL | id, sim);
        }
    }

    // ── World-provider session hooks (spawn/destroy resource integration) ──────

    void IWorldSessionHooks.OnSpawned(SceneNode subtreeRoot)
    {
        RegisterBodies(subtreeRoot); // UsePhysics nodes in the spawned subtree get live Jolt bodies
        InitAudioSources(subtreeRoot);
        _vfx.Add(subtreeRoot);
    }

    void IWorldSessionHooks.OnDestroying(SceneNode subtreeRoot)
    {
        ReleaseNodeResources(subtreeRoot);
        _vfx.Remove(subtreeRoot);
    }

    private void Snapshot(SceneNode node)
    {
        _savedPos[node.Id] = node.Position;
        _savedRot[node.Id] = node.Rotation;
        _savedScl[node.Id] = node.Scale;
        foreach (var c in node.Children) Snapshot(c);
    }

    private void RegisterBodies(SceneNode node)
    {
        if (node.UsePhysics)
        {
            var euler = node.Rotation.ToEulerRadians();
            var motion = node.IsStatic ? PhysicsMotionType.Static : PhysicsMotionType.Dynamic;
            var bodyId = _physics.CreateAndAddBody(
                new PhysicsBodySettings {
                    ShapeType = node.PhysicsShape,
                    HalfExtents = node.PhysicsHalfExtents,
                    Position = node.Position,
                    Rotation = euler,
                    MotionType = motion,
                    Friction = node.PhysicsFriction,
                    Restitution = node.PhysicsRestitution,
                    GravityFactor = node.UseGravity ? 1f : 0f,
                    Mass = node.PhysicsMass,
                }
            );
            _bodyIds[node.Id] = (node, bodyId);
        }

        foreach (var c in node.Children) RegisterBodies(c);
    }

    // ── Restore on Stop ───────────────────────────────────────────────────────

    public void Restore(SceneNode root)
    {
        _physDriver.Restore(); // hand the render settings back to SettingsPanel before leaving play
        Teardown();
        RestoreNode(root);
    }

    /// <summary>
    ///     Release every resource and global the session acquired: unwire the static providers, revert
    ///     GPU instancing, drop the overlay/debug queues, and dispose the script world + native Jolt
    ///     world. Used by both <see cref="Restore" /> (normal stop) and the constructor's failure path,
    ///     so it must tolerate a partially-built session (null-guards on the late-assigned backends).
    /// </summary>
    private void Teardown()
    {
        Input.Axis2DProvider = null;
        Input.KeyDownProvider = null;
        Input.LookDeltaProvider = null;
        Gamepad.ConnectedProvider = null;
        Gamepad.AxisProvider = null;
        Gamepad.ButtonProvider = null;
        Physics.Backend = null;
        Scripting.World.Backend =
            null; // before scripts dispose: World calls in OnDestroy become no-ops
        Scenes.Backend = null; // likewise — no scene swaps while tearing down
        _instancing?.ClearAll(); // revert instanced nodes to single draws for edit mode
        Instancing.Backend = null;
        RenderView.Clear();
        Hud.Reset(); // drop the immediate queue AND the retained widget HUD so nothing lingers into edit mode
        DebugDraw.Clear(); // drop queued debug lines + disable
        _scripts.Dispose(); // runs each component's OnDestroy — Audio.Backend is still live here so a
        // SoundEmitter can release its own source. Tear down audio after, then silence anything left.
        foreach (var (_, sound) in _audioSources.Values)
            ZigoteEngine.Instance?.AudioSoundDestroy(sound);
        _audioSources.Clear();
        Music.Reset(); // frees its tracks — must run while Audio.Backend is still live
        Audio.Backend = null;
        Camera.Backend = null;
        // Save stays live through _scripts.Dispose (a game may quit-save in OnDestroy); clear after.
        Scripting.Save.Store = null;
        Scripting.Save.DefaultDirectory = null;
        ZigoteEngine.Instance?.AudioStopAll();
        // Undo play's structural scene edits (remove spawned nodes, re-attach destroyed authored ones)
        // after OnDestroy ran but before the TRS snapshot restore walks the tree.
        _world?.RestoreSceneEdits();
        Scripting.Vfx.Backend = null;
        _vfx.Reset(); // drop the live particle pools (no native resources held)
        ZigoteEngine.Instance?.ParticlesClearAll(); // drop any native billboard batches we uploaded
        Sprites.Clear(); // drop the script tick-queue + camera override (after OnDestroy, which may Destroy textures)
        Sprites.Backend = null;
        _sprites?.ResetPlayState(); // session animation clocks; the host's texture/shader caches stay warm
        _physics.Dispose();
        _bodyIds.Clear();
        Scripting.Ecs.World = null; // unwire the provider before destroying the world it points at
        Scripting.Ecs.Scene = null;
        Scripting.Ecs.Prefabs = null;
        Ecs?.Dispose(); // destroy the flecs world mirroring the scene
        Ecs = null;
    }

    private void RestoreNode(SceneNode node)
    {
        if (_savedPos.TryGetValue(node.Id, out var p)) node.Position = p;
        if (_savedRot.TryGetValue(node.Id, out var r)) node.Rotation = r;
        if (_savedScl.TryGetValue(node.Id, out var s)) node.Scale = s;
        foreach (var c in node.Children) RestoreNode(c);
    }

    // ── Per-frame update ─────────────────────────────────────────────────────

    // Input providers read this session's live movement fields. Wired ONCE (not per frame) so the
    // play hot loop doesn't allocate a fresh closure (+ a ToLowerInvariant string) every frame.
    private void WireInputProviders()
    {
        Input.Axis2DProvider = name => name.ToLowerInvariant() switch {
            "move" or "horizontal" => new Vec2(
                (MoveRight ? 1f : 0f) - (MoveLeft ? 1f : 0f),
                (MoveForward ? 1f : 0f) - (MoveBack ? 1f : 0f)
            ),
            _ => Vec2.Zero,
        };
        Input.KeyDownProvider = name => name.ToLowerInvariant() switch {
            "w" or "forward" or "up" or "throttle" or "accelerate" => MoveForward,
            "s" or "back" or "backward" or "brake" or "reverse" => MoveBack,
            "a" or "left" or "steerleft" => MoveLeft,
            "d" or "right" or "steerright" => MoveRight,
            "e" or "ascend" => MoveUp,
            "q" or "descend" => MoveDown,
            "space" or "handbrake" => Handbrake,
            "r" or "reset" => ResetCar,
            // Anything else falls through to the general held-key set, so a game can bind whatever
            // it likes without the host knowing about it.
            var other => HeldKeys.Contains(other),
        };

        // Mouse-look delta (right-drag) for scripts that own the camera (e.g. orbit a chase cam).
        Input.LookDeltaProvider = () => new Vec2(LookDx, LookDy);

        // Generic game-controller input (SDL gamepad), read from the native engine each query.
        // OPT-IN: initializing SDL's gamepad subsystem with a controller connected can hang on some
        // macOS setups (the IOKit-HID / Input-Monitoring permission path). Wiring the providers is what
        // first drives the native query → SDL gamepad init, so leaving them unwired means play mode never
        // touches that path and therefore can never hang. Keyboard/mouse driving is unaffected. Set
        // ZIGOTE_GAMEPAD=1 to enable controller input once it's confirmed safe on the machine.
        if (Environment.GetEnvironmentVariable("ZIGOTE_GAMEPAD") == "1")
        {
            Gamepad.ConnectedProvider = () => ZigoteEngine.Instance?.GamepadConnected() ?? false;
            Gamepad.AxisProvider = axis => ZigoteEngine.Instance?.GamepadAxis(0, axis) ?? 0f;
            Gamepad.ButtonProvider =
                button => ZigoteEngine.Instance?.GamepadButton(0, button) ?? false;
        }
        else
        {
            Console.WriteLine(
                "[Zigote] Gamepad input off (set ZIGOTE_GAMEPAD=1 to enable) — keyboard/mouse controls active."
            );
        }
    }

    public void Update(SceneNode root, float dt)
    {
        // Found once and passed down. FindCamera walks the tree until it hits a Camera node, and this
        // ran twice per render frame — here and inside PublishRenderView — for an answer that cannot
        // change between the two calls. On a large scene with the camera late in the tree that is a
        // full traversal per frame spent re-deriving what was just derived.
        var cam = FindCamera(root);

        // 0. Publish the camera once per render frame so scripts can do view-dependent work (frustum
        //    culling / LOD). One-frame-stale transform + last paint's viewport size — invisible for culling.
        PublishRenderView(root, cam, dt);

        // Whether a game script owns the Camera node decides who drives the camera and who consumes the
        // mouse-look delta: a chase-cam script reads it inside the fixed loop, the free-fly fallback after.
        var freeFlyCamera = cam != null && _scripts.GetComponents(cam.Id).Count == 0;

        // 1. Fixed-timestep core. Advance gameplay + physics in constant FixedDt slices, so behaviour is
        //    frame-rate independent. The render dt only sets how many slices run; the remainder carries over.
        _accumulator = MathF.Min(_accumulator + dt, MaxCatchUp);
        var tick = 0;
        while (_accumulator >= FixedDt)
        {
            // Immediate-mode debug-draw queue: clear at the start of each tick so the render frame draws the
            // LAST tick's lines (re-emitting into a live queue would stack one copy per tick). The widget HUD
            // (Hud.Root) is retained, not a per-tick queue, so it needs no reset here.
            DebugDraw.BeginFrame();
            Sprites.BeginTick(); // same tick-queue model: the viewport READS the last completed tick's draws
            _world?.BeginTick(); // advances the World provider's spatial-query rebuild stamp

            // Scripts (gameplay: vehicle, chase camera, HUD, etc.) first so a control script's inputs apply
            // on the same tick, then physics. One collision sub-step is plenty at 120 Hz (≈8 ms slices).
            _scripts.Update(root, FixedDt);
            _world?.ApplyDeferred(); // World.Destroy/SetParent queued by scripts — before physics steps dead bodies
            _scenes?.ApplyPending(); // a requested scene swap runs at the same safe point
            _physics.Step(FixedDt);
            SyncFromPhysics();
            _vfx.Step(
                FixedDt
            ); // node emitters: step after transforms settle so spawn origins are current
            _vfxBackend.Step(FixedDt); // script-spawned emitters
            _sprites?.AdvanceAnimation(
                root,
                FixedDt
            ); // session-side frame clocks — never the authored SpriteFrame
            _accumulator -= FixedDt;

            // Mouse-look is a once-per-render-frame delta: when a script owns the camera, let only the first
            // tick see it so a slow frame's many ticks don't multiply the look sensitivity.
            if (++tick == 1 && !freeFlyCamera)
            {
                LookDx = 0f;
                LookDy = 0f;
            }
        }

        // Publish the leftover sub-tick time as a fraction of a tick, so a render-side script can
        // interpolate between the last two ticks' states (0-tick frames repeat identical state).
        Time._interpolationAlpha = _accumulator / FixedDt;

        // 2. Camera free-fly fallback (empty scenes stay navigable): once per render frame at the render dt
        //    — a view convenience, not simulation — consuming the frame's still-untouched look delta.
        if (freeFlyCamera) TickCamera(cam!, dt);

        // Entity-first transform hand-off. Bake the settled node tree (scripts + physics + camera) into the
        // canonical entity Transforms (change-gated inside the bridge: an unchanged node costs no FFI
        // write); an ECS-system pass would run here and mutate them; then mirror the canonical Transforms
        // back onto the nodes the renderer reads. With no systems (or observers) registered nothing
        // entity-side can have written a Transform, so the pull half is skipped whole.
        Ecs?.PushTransforms(root);
        if (Ecs is { } ecs && ecs.World.SystemCount > 0) ecs.PullTransforms(root);

        root.SyncToNative();

        // Scene-transition fade advances at render rate (it's visual, not simulation).
        _scenes?.TickFade(dt);

        // 3. Audio: keep editor-authored spatial sources glued to their (now-updated) node transforms, then
        //    service the engine so fire-and-forget one-shots get reaped. Music fades ride the render dt.
        UpdateAudioSources();
        Music.Tick(dt);
        ZigoteEngine.Instance?.AudioUpdate(dt);

        // Consume any look delta a script-owned camera left (or that 0 ticks this frame never read).
        LookDx = 0;
        LookDy = 0;
    }

    // ── Audio sources (editor-authored AudioSource nodes) ──────────────────────

    private void InitAudioSources(SceneNode node)
    {
        if (node.Kind == NodeKind.AudioSource) CreateAudioSource(node);
        foreach (var c in node.Children) InitAudioSources(c);
    }

    private void CreateAudioSource(SceneNode node)
    {
        var engine = ZigoteEngine.Instance;
        if (engine == null) return;

        uint id;
        if (node.AudioUseFile)
        {
            if (string.IsNullOrEmpty(node.AudioClipPath)) return;
            var path = Path.IsPathRooted(node.AudioClipPath)
                ? node.AudioClipPath
                : Path.GetFullPath(node.AudioClipPath);
            if (!File.Exists(path)) return;
            id = engine.AudioSoundCreateFile(path, node.AudioStreaming);
        }
        else
        {
            id = engine.AudioSoundCreateTone(
                node.AudioFrequency,
                Math.Clamp(node.AudioWaveform, 0, 4)
            );
        }

        if (id == 0) return;
        engine.AudioSoundSetSpatial(id, node.AudioSpatial);
        engine.AudioSoundSetVolume(id, node.AudioVolume);
        engine.AudioSoundSetPitch(id, node.AudioPitch);
        engine.AudioSoundSetLooping(id, node.AudioLoop);
        if (node.AudioSpatial)
        {
            engine.AudioSoundSetAttenuation(
                id,
                node.AudioMinDistance,
                node.AudioMaxDistance,
                node.AudioRolloff
            );
            engine.AudioSoundSetPosition(id, WorldTransform(node).Position);
        }

        _audioSources[node.Id] = (node, id);
        if (node.AudioAutoPlay) engine.AudioSoundPlay(id);
    }

    /// <summary>
    ///     Glue every spatial source to its node's world transform, once per render frame.
    ///     <para>
    ///         Over the index rather than the scene: this used to walk the whole node tree looking for
    ///         the handful of nodes that are <c>AudioSource</c>s, every frame, when the sources were
    ///         already indexed by the walk that created them. A scene is thousands of nodes and a
    ///         soundscape is tens of sources.
    ///     </para>
    /// </summary>
    private void UpdateAudioSources()
    {
        if (_audioSources.Count == 0 || ZigoteEngine.Instance is not { } engine) return;
        foreach (var (node, sound) in _audioSources.Values)
            if (node.AudioSpatial)
                engine.AudioSoundSetPosition(sound, WorldTransform(node).Position);
    }

    /// <summary>
    ///     Drop every latched keyboard movement/look input. Keys are routed only to the focused widget
    ///     (App dispatches to <c>_focusedWidget</c>), and these flags are set on key-down / cleared on
    ///     key-up by the viewport. If focus leaves the viewport while a drive key (WASD / Space) is held,
    ///     the matching key-up is delivered elsewhere and the flag would stick "on" — making the car
    ///     accelerate, brake or steer by itself. The viewport calls this when it loses focus.
    /// </summary>
    public void ResetInput()
    {
        MoveForward = MoveBack = MoveLeft = MoveRight = MoveUp = MoveDown = false;
        Handbrake = ResetCar = false;
        LookDx = LookDy = 0f;
        HeldKeys.Clear();
    }

    private void PublishRenderView(SceneNode root, SceneNode? cam, float dt)
    {
        if (cam == null)
        {
            _physDriver.Restore(); // camera gone → hand the global settings back to SettingsPanel
            RenderView.Clear(); // camera deleted mid-play → scripts can detect and skip view work
            return;
        }

        var aspect = RenderView.ViewportHeight > 0f
            ? RenderView.ViewportWidth / RenderView.ViewportHeight
            : 16f / 9f;
        // Match the native renderer exactly: WORLD transform (parent-baked), forward = worldRot·(0,0,-1),
        // and WORLD up (not the camera's rolled up). The renderer builds its view the same way
        // (wgpu_3d.zig: lookAt(world.pos, world.pos + world.rot·forward, Vec3.up)), so the frustum
        // we publish culls exactly what is drawn — including for a camera parented under a rig.
        var world = WorldTransform(cam);
        var fwd = world.Rotation.RotateVec(Vec3.Forward);
        var view = Mat4.LookAt(world.Position, world.Position + fwd, Vec3.Up);

        // FOV lockstep: the published frustum MUST use the same FOV/near/far the renderer draws with.
        // The renderer reads them from the camera node (SceneNode.SyncToNative pushes EffectiveFovDegrees
        // via SceneSetCameraParams), so build the projection from the same per-camera values — never the
        // old hardcoded 45°/0.1/4000 constants — or culling diverges from the image once FOV is dynamic.
        float fovRad, near = cam.CameraNear, far = cam.CameraFar;
        var grade = _physDriver.Apply(
            cam,
            world.Position,
            RenderView.ViewportHeight,
            dt,
            id => FindNodeById(root, id)
        );
        if (grade is { } g)
        {
            fovRad = g.FovYRadians;
            near = g.NearM;
            far = g.FarM;
        }
        else
        {
            fovRad = cam.CameraFovDegrees * (MathF.PI / 180f);
        }

        // Orthographic cameras (2D games): publish the ortho view-proj so RenderView consumers
        // (DebugDraw projection, script culling) match the 2D framing. Note the native 3D mesh pass
        // itself still renders perspective — a documented limit; pure-2D scenes have no meshes to care.
        var proj = cam.CameraProjection == 1
            ? Mat4.OrthographicRhZo(
                -cam.CameraOrthoSize.Y * 0.5f * aspect,
                cam.CameraOrthoSize.Y * 0.5f * aspect,
                -cam.CameraOrthoSize.Y * 0.5f,
                cam.CameraOrthoSize.Y * 0.5f,
                -1000f,
                1000f
            )
            : Mat4.PerspectiveRhZo(
                fovRad,
                aspect,
                near,
                far
            );
        RenderView.Set(
            proj * view,
            world.Position,
            RenderView.ViewportWidth,
            RenderView.ViewportHeight
        );

        // The spatial-audio listener rides the active camera, so surround panning matches what's on screen.
        var up = world.Rotation.RotateVec(Vec3.Up);
        Audio.SetListener(world.Position, fwd, up);
    }

    private static SceneNode? FindNodeById(SceneNode node, int id)
    {
        if (node.Id == id) return node;
        foreach (var c in node.Children)
        {
            var found = FindNodeById(c, id);
            if (found != null) return found;
        }

        return null;
    }

    /// World transform of a node (parent-baked), matching how native composes node.world_transform.
    private static Transform3D WorldTransform(SceneNode node)
    {
        var local = new Transform3D(node.Position, node.Rotation, node.Scale);
        return node.Parent is { } parent
            ? Transform3D.Combine(WorldTransform(parent), local)
            : local;
    }

    /// <summary>
    ///     Read every dynamic body's transform back onto its node, once per fixed step.
    ///     <para>
    ///         The list is gathered from the body index rather than by walking the scene: static
    ///         geometry is most of a level and none of it moves, so a traversal per tick was
    ///         proportional to the wrong number. Order is irrelevant — each body writes only its own
    ///         node — so the dictionary's is as good as the tree's.
    ///     </para>
    /// </summary>
    private void SyncFromPhysics()
    {
        _syncBodies.Clear();
        foreach (var (node, bodyId) in _bodyIds.Values)
            if (bodyId != PhysicsWorld.InvalidBodyId && !node.IsStatic)
                _syncBodies.Add((node, bodyId));

        var count = _syncBodies.Count;
        if (count == 0) return;

        var ids = _syncIds.Get(count);
        for (var i = 0; i < count; i++) ids[i] = _syncBodies[i].BodyId;
        var xforms = _syncXforms.Get(count * 7);
        _physics.GetBodyTransforms(ids, xforms);

        for (var i = 0; i < count; i++)
        {
            var node = _syncBodies[i].Node;
            var b = i * 7;
            node.Position = new Vec3(xforms[b], xforms[b + 1], xforms[b + 2]);
            node.Rotation = new Quat(
                xforms[b + 3],
                xforms[b + 4],
                xforms[b + 5],
                xforms[b + 6]
            );
        }
    }

    private void TickCamera(SceneNode cam, float dt)
    {
        _camYaw += LookDx * LookSens;
        _camPitch -= LookDy * LookSens;
        _camPitch = Math.Clamp(_camPitch, -1.45f, 1.45f);

        var fwd = new Vec3(-MathF.Sin(_camYaw), 0f, -MathF.Cos(_camYaw));
        var right = new Vec3(MathF.Cos(_camYaw), 0f, -MathF.Sin(_camYaw));

        var move = Vec3.Zero;
        if (MoveForward) move = move + fwd;
        if (MoveBack) move = move - fwd;
        if (MoveRight) move = move + right;
        if (MoveLeft) move = move - right;
        if (MoveUp) move = move + Vec3.Up;
        if (MoveDown) move = move - Vec3.Up;

        if (move.LengthSq() > 0f)
            cam.Position = cam.Position + move.Normalize() * (CameraSpeed * dt);

        cam.Rotation = Quat.FromEuler(_camPitch, _camYaw, 0f);
    }

    private static SceneNode? FindCamera(SceneNode node)
    {
        if (node.Kind == NodeKind.Camera) return node;
        foreach (var c in node.Children)
        {
            var found = FindCamera(c);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>Apply an instantaneous impulse to a physics-enabled node.</summary>
    public void ApplyImpulse(int nodeId, Vec3 impulse)
    {
        if (_bodyIds.TryGetValue(nodeId, out var body) &&
            body.BodyId != PhysicsWorld.InvalidBodyId)
            _physics.AddImpulse(body.BodyId, impulse);
    }

    /// <summary>Live-tune a running script: apply a changed exported field to the node's components.</summary>
    public void ApplyExportedField(int nodeId, ExportedField field, string json)
    {
        _scripts.ApplyExportedField(nodeId, field, json);
    }

    private void ReleaseNodeResources(SceneNode node)
    {
        if (_bodyIds.Remove(node.Id, out var body) && body.BodyId != PhysicsWorld.InvalidBodyId)
            _physics.DestroyBody(body.BodyId);
        if (_audioSources.Remove(node.Id, out var source))
            ZigoteEngine.Instance?.AudioSoundDestroy(source.Sound);
        foreach (var c in node.Children) ReleaseNodeResources(c);
    }
}