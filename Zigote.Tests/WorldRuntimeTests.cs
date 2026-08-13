using Xunit;
using Zigote.Core.Math3D;
using Zigote.Ecs.Scene;
using Zigote.Runtime.Prefab;
using Zigote.Runtime.Scene;
using Zigote.Scripting;
using Zigote.Scripting.Metadata;
// See WorldProviderTests: inside Zigote.* the bare `World` binds to the Zigote.World namespace.
using GameWorld = Zigote.Scripting.World;

namespace Zigote.Tests;

/// <summary>
///     Lifecycle probe attached by prefab spawns (top-level public so ScriptRegistry discovers
///     it).
/// </summary>
public sealed class WorldProbeComponent : Component
{
    public bool Created;
    public bool Destroyed;
    public int Updates;

    protected override void OnCreate() => Created = true;

    protected override void OnUpdate(float dt) => Updates++;

    protected override void OnDestroy() => Destroyed = true;
}

/// <summary>Spawns one prefab from inside OnUpdate — exercises mid-walk tree mutation.</summary>
public sealed class WorldSpawnerComponent : Component
{
    public static string? PrefabPath;
    public EntityHandle Spawned;

    protected override void OnUpdate(float dt)
    {
        if (!Spawned.IsValid && PrefabPath != null)
        {
            Spawned = GameWorld.Spawn(
                prefabPath: PrefabPath,
                position: new Vec3(x: 5f, y: 0f, z: 0f)
            );
        }
    }
}

/// <summary>Destroys its own entity from inside OnUpdate — exercises the deferred-destroy path.</summary>
public sealed class WorldSelfDestructComponent : Component
{
    protected override void OnUpdate(float dt) => GameWorld.Destroy(GameWorld.Of(this));
}

/// <summary>
///     Integration tests for <see cref="RuntimeWorldBackend" /> over a real SceneNode tree, a real
///     <see cref="ScriptWorld" />, and a real flecs <see cref="EcsSceneBridge" /> (needs libzigote,
///     like EcsWorldTests — no native *engine* is initialized). Session hooks are a recording fake.
/// </summary>
public sealed class WorldRuntimeTests : IDisposable
{
    private readonly RuntimeWorldBackend _backend;
    private readonly string _dir;
    private readonly EcsSceneBridge _ecs;
    private readonly RecordingHooks _hooks = new();
    private readonly string _prefabPath;
    private readonly SceneNode _root;
    private readonly ScriptWorld _scripts;

    public WorldRuntimeTests()
    {
        _dir = Directory.CreateTempSubdirectory("zigote-world-tests").FullName;

        _root = new SceneNode("Scene");
        _root.AddChild(
            new SceneNode("Player") {
                Tag = "Player",
                Position = new Vec3(x: 0f, y: 0f, z: 0f),
            }
        );
        _root.AddChild(
            new SceneNode("Rock") {
                Tag = "Obstacle",
                Position = new Vec3(x: 10f, y: 0f, z: 0f),
            }
        );

        var registry = new ScriptRegistry();
        registry.Load(typeof(WorldRuntimeTests).Assembly);
        _scripts = new ScriptWorld(registry);

        _ecs = new EcsSceneBridge();
        _ecs.BuildFrom(_root); // same order as GameSession: bridge first, then the backend
        _backend = new RuntimeWorldBackend(
            root: _root,
            scripts: _scripts,
            ecs: _ecs,
            hooks: _hooks
        );

        _prefabPath = Path.Combine(path1: _dir, path2: "bullet.prefab");
        var template = new SceneNode("Bullet") {
            Tag = "Projectile",
            ScriptClass = typeof(WorldProbeComponent).FullName,
        };
        template.AddChild(new SceneNode("Fin"));
        new PrefabDocument {
            Name = "Bullet",
            Template = template,
        }.Save(_prefabPath);
    }

    public void Dispose()
    {
        _ecs.Dispose();
        try
        {
            Directory.Delete(path: _dir, recursive: true);
        }
        catch (IOException) { }
    }

    private static void Tick(RuntimeWorldBackend backend)
    {
        backend.BeginTick();
        backend.ApplyDeferred();
    }

    // ── Spawn ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Spawn_FromPrefab_CreatesALiveIntegratedEntity()
    {
        var h = _backend.Spawn(
            prefabPath: _prefabPath,
            position: new Vec3(x: 1f, y: 2f, z: 3f),
            rotation: Quat.Identity,
            parent: EntityHandle.None
        );

        Assert.True(h.IsValid);
        Assert.True(_backend.IsAlive(h));
        Assert.Equal(expected: "Bullet", actual: _backend.GetName(h));
        Assert.Equal(expected: new Vec3(x: 1f, y: 2f, z: 3f), actual: _backend.GetPosition(h));

        // In the tree, under the root, with its child intact
        var node = _root.Children.Single(c => c.Name == "Bullet");
        Assert.Single(node.Children); // "Fin"

        // Scripts attached and OnCreate ran inside Spawn
        var probe =
            Assert.IsType<WorldProbeComponent>(
                _backend.GetComponent(entity: h, type: typeof(WorldProbeComponent))
            );
        Assert.True(probe.Created);

        // Session hooks saw the subtree
        Assert.Equal(expected: ["Bullet"], actual: _hooks.Spawned);

        // flecs mirror: a live entity carrying the canonical Transform, parented under the root's entity
        var e = _backend.EcsEntity(h);
        Assert.False(e.IsNull);
        Assert.True(_ecs.World.IsAlive(e));
        Assert.Equal(expected: _ecs.EntityOf(_root.Id), actual: _ecs.World.GetParent(e));

        // Template tag seeded the index
        Assert.Equal(expected: 1, actual: _backend.CountByTag("Projectile"));
    }

    [Fact]
    public void Spawn_UnderParent_ParentsAndBakesWorldPosition()
    {
        var parent = _backend.Find("Player");
        _backend.SetPosition(entity: parent, position: new Vec3(x: 100f, y: 0f, z: 0f));

        var h = _backend.Spawn(
            prefabPath: _prefabPath,
            position: new Vec3(x: 1f, y: 0f, z: 0f),
            rotation: Quat.Identity,
            parent: parent
        );

        Assert.Equal(expected: parent, actual: _backend.GetParent(h));
        Assert.Equal(
            expected: new Vec3(x: 1f, y: 0f, z: 0f),
            actual: _backend.GetPosition(h)
        ); // local
        Assert.Equal(
            expected: new Vec3(x: 101f, y: 0f, z: 0f),
            actual: _backend.GetWorldPosition(h)
        ); // parent-baked
    }

    [Fact]
    public void Spawn_MissingPrefab_ReturnsNone()
    {
        Assert.Equal(
            expected: EntityHandle.None,
            actual: _backend.Spawn(
                prefabPath: Path.Combine(path1: _dir, path2: "nope.prefab"),
                position: Vec3.Zero,
                rotation: Quat.Identity,
                parent: EntityHandle.None
            )
        );
    }

    [Fact]
    public void Spawn_UnderDeadParent_ReturnsNone()
    {
        var h = _backend.SpawnEmpty(name: "Mount", position: Vec3.Zero, parent: EntityHandle.None);
        _backend.Destroy(h);
        Tick(_backend);

        Assert.Equal(
            expected: EntityHandle.None,
            actual: _backend.Spawn(
                prefabPath: _prefabPath,
                position: Vec3.Zero,
                rotation: Quat.Identity,
                parent: h
            )
        );
    }

    [Fact]
    public void SpawnEmpty_Plus_AddComponent()
    {
        var h = _backend.SpawnEmpty(
            name: "Mount",
            position: new Vec3(x: 2f, y: 0f, z: 0f),
            parent: EntityHandle.None
        );

        var comp = _backend.AddComponent(entity: h, type: typeof(WorldProbeComponent));
        var probe = Assert.IsType<WorldProbeComponent>(comp);
        Assert.True(probe.Created);
        Assert.Same(
            expected: probe,
            actual: _backend.GetComponent(entity: h, type: typeof(WorldProbeComponent))
        );
    }

    // ── Destroy (deferred) ────────────────────────────────────────────────────

    [Fact]
    public void Destroy_IsDeferred_ThenTearsEverythingDown()
    {
        var h = _backend.Spawn(
            prefabPath: _prefabPath,
            position: Vec3.Zero,
            rotation: Quat.Identity,
            parent: EntityHandle.None
        );
        var probe = (WorldProbeComponent)_backend.GetComponent(
            entity: h,
            type: typeof(WorldProbeComponent)
        )!;
        var e = _backend.EcsEntity(h);

        _backend.Destroy(h);
        Assert.True(_backend.IsAlive(h)); // handles stay valid until the tick ends

        Tick(_backend);

        Assert.False(_backend.IsAlive(h));
        Assert.True(probe.Destroyed);
        Assert.DoesNotContain(collection: _root.Children, filter: c => c.Name == "Bullet");
        Assert.Equal(expected: ["Bullet"], actual: _hooks.Destroying);
        Assert.False(_ecs.World.IsAlive(e));
        Assert.Equal(expected: 0, actual: _backend.CountByTag("Projectile"));
    }

    [Fact]
    public void Destroy_TheRoot_IsRefused()
    {
        _backend.Destroy(new EntityHandle((uint)_root.Id));
        Tick(_backend);
        Assert.True(_backend.IsAlive(new EntityHandle((uint)_root.Id)));
    }

    [Fact]
    public void Destroy_Twice_IsHarmless()
    {
        var h = _backend.Spawn(
            prefabPath: _prefabPath,
            position: Vec3.Zero,
            rotation: Quat.Identity,
            parent: EntityHandle.None
        );
        _backend.Destroy(h);
        _backend.Destroy(h);
        Tick(_backend);
        Assert.False(_backend.IsAlive(h));
        Assert.Single(_hooks.Destroying);
    }

    // ── Play-stop scene restore ───────────────────────────────────────────────

    [Fact]
    public void RestoreSceneEdits_RemovesSpawnedNodes()
    {
        int authoredCount = _root.Children.Count;
        _backend.Spawn(
            prefabPath: _prefabPath,
            position: Vec3.Zero,
            rotation: Quat.Identity,
            parent: EntityHandle.None
        );
        _backend.Spawn(
            prefabPath: _prefabPath,
            position: Vec3.Zero,
            rotation: Quat.Identity,
            parent: EntityHandle.None
        );

        _backend.RestoreSceneEdits();

        Assert.Equal(expected: authoredCount, actual: _root.Children.Count);
        Assert.DoesNotContain(collection: _root.Children, filter: c => c.Name == "Bullet");
    }

    [Fact]
    public void RestoreSceneEdits_ReattachesADestroyedAuthoredNode()
    {
        var rock = _backend.Find("Rock");
        _backend.Destroy(rock);
        Tick(_backend);
        Assert.DoesNotContain(collection: _root.Children, filter: c => c.Name == "Rock");

        _backend.RestoreSceneEdits();

        Assert.Equal(
            expected: "Rock",
            actual: _root.Children[1].Name
        ); // back at its original index
        Assert.Same(expected: _root, actual: _root.Children[1].Parent);
    }

    [Fact]
    public void RestoreSceneEdits_ReattachesSpawnNestedUnderDestroyedAuthored()
    {
        var rock = _backend.Find("Rock");
        var spawned = _backend.Spawn(
            prefabPath: _prefabPath,
            position: Vec3.Zero,
            rotation: Quat.Identity,
            parent: rock
        );
        Assert.True(spawned.IsValid);

        _backend.Destroy(rock);
        Tick(_backend);
        Assert.False(_backend.IsAlive(spawned)); // died with its parent, with full teardown
        Assert.Contains(expected: "Bullet", collection: _hooks.Destroying);

        _backend.RestoreSceneEdits();
        var restored = _root.Children.Single(c => c.Name == "Rock");
        Assert.DoesNotContain(
            collection: restored.Children,
            filter: c => c.Name == "Bullet"
        ); // spawn not resurrected
    }

    [Fact]
    public void RestoreSceneEdits_RestoresVisibility()
    {
        var rock = _backend.Find("Rock");
        _backend.SetVisible(entity: rock, visible: false);
        Assert.False(_backend.GetVisible(rock));

        _backend.RestoreSceneEdits();
        Assert.True(_root.Children.Single(c => c.Name == "Rock").Visible);
    }

    // ── Reparent (deferred) ───────────────────────────────────────────────────

    [Fact]
    public void SetParent_IsDeferred_AndRestoredOnStop()
    {
        var rock = _backend.Find("Rock");
        var player = _backend.Find("Player");

        _backend.SetParent(child: rock, parent: player);
        Assert.Equal(
            expected: new EntityHandle((uint)_root.Id),
            actual: _backend.GetParent(rock)
        ); // not yet

        Tick(_backend);
        Assert.Equal(expected: player, actual: _backend.GetParent(rock));

        _backend.RestoreSceneEdits();
        Assert.Equal(expected: "Rock", actual: _root.Children[1].Name); // authored structure back
    }

    [Fact]
    public void SetParent_RefusesCycles()
    {
        var player = _backend.Find("Player");
        var child = _backend.Spawn(
            prefabPath: _prefabPath,
            position: Vec3.Zero,
            rotation: Quat.Identity,
            parent: player
        );

        _backend.SetParent(child: player, parent: child);
        Tick(_backend);

        Assert.Equal(
            expected: new EntityHandle((uint)_root.Id),
            actual: _backend.GetParent(player)
        );
    }

    // ── Find / tags / spatial ─────────────────────────────────────────────────

    [Fact]
    public void Find_ReturnsFirstLiveMatchInTreeOrder()
    {
        Assert.Equal(expected: "Player", actual: _backend.GetName(_backend.Find("Player")));
        Assert.Equal(expected: EntityHandle.None, actual: _backend.Find("Ghost"));
    }

    [Fact]
    public void Tags_SeededFromAuthoredNodes_AndSessionRetaggable()
    {
        Assert.Equal(expected: 1, actual: _backend.CountByTag("Obstacle"));

        var rock = _backend.Find("Rock");
        _backend.SetTag(entity: rock, tag: "Rubble");
        Assert.Equal(expected: 0, actual: _backend.CountByTag("Obstacle"));
        Assert.Equal(expected: "Rubble", actual: _backend.GetTag(rock));

        // Session-local: the authored node's Tag was never touched
        Assert.Equal(
            expected: "Obstacle",
            actual: _root.Children.Single(c => c.Name == "Rock").Tag
        );

        var results = new List<EntityHandle>();
        Assert.Equal(expected: 1, actual: _backend.FindAllByTag(tag: "Rubble", results: results));
        Assert.Equal(expected: rock, actual: results[0]);
    }

    [Fact]
    public void OverlapSphere_UsesWorldPositions_AndTagFilter()
    {
        _backend.BeginTick();
        var results = new List<EntityHandle>();

        // Player at 0, Rock at 10 — radius 5 around origin hits only the Player
        Assert.Equal(
            expected: 1,
            actual: _backend.OverlapSphere(
                center: Vec3.Zero,
                radius: 5f,
                results: results,
                tag: null
            )
        );
        Assert.Equal(expected: _backend.Find("Player"), actual: results[0]);

        // Tag filter
        Assert.Equal(
            expected: 0,
            actual: _backend.OverlapSphere(
                center: Vec3.Zero,
                radius: 5f,
                results: results,
                tag: "Obstacle"
            )
        );
        Assert.Equal(
            expected: 1,
            actual: _backend.OverlapSphere(
                center: Vec3.Zero,
                radius: 50f,
                results: results,
                tag: "Obstacle"
            )
        );

        // Position writes invalidate the per-tick index
        _backend.SetPosition(
            entity: _backend.Find("Rock"),
            position: new Vec3(x: 1f, y: 0f, z: 0f)
        );
        Assert.Equal(
            expected: 1,
            actual: _backend.OverlapSphere(
                center: Vec3.Zero,
                radius: 5f,
                results: results,
                tag: "Obstacle"
            )
        );
    }

    [Fact]
    public void Nearest_FindsClosest_HonoursTagAndIgnore()
    {
        _backend.BeginTick();
        var player = _backend.Find("Player");
        var rock = _backend.Find("Rock");

        Assert.Equal(
            expected: player,
            actual: _backend.Nearest(
                center: new Vec3(x: 1f, y: 0f, z: 0f),
                maxRadius: 100f,
                tag: null,
                ignore: EntityHandle.None
            )
        );
        Assert.Equal(
            expected: rock,
            actual: _backend.Nearest(
                center: new Vec3(x: 1f, y: 0f, z: 0f),
                maxRadius: 100f,
                tag: null,
                ignore: player
            )
        );
        Assert.Equal(
            expected: rock,
            actual: _backend.Nearest(
                center: new Vec3(x: 1f, y: 0f, z: 0f),
                maxRadius: 100f,
                tag: "Obstacle",
                ignore: EntityHandle.None
            )
        );
        Assert.Equal(
            expected: EntityHandle.None,
            actual: _backend.Nearest(
                center: new Vec3(x: 1f, y: 0f, z: 0f),
                maxRadius: 2f,
                tag: "Obstacle",
                ignore: EntityHandle.None
            )
        );
    }

    [Fact]
    public void FindComponent_ScansTheTree()
    {
        Assert.Null(_backend.FindComponent(typeof(WorldProbeComponent)));
        _backend.Spawn(
            prefabPath: _prefabPath,
            position: Vec3.Zero,
            rotation: Quat.Identity,
            parent: EntityHandle.None
        );
        Assert.IsType<WorldProbeComponent>(_backend.FindComponent(typeof(WorldProbeComponent)));
    }

    // ── Scripts driving the API mid-tick (provider wired, like play mode) ─────

    [Fact]
    public void Component_CanSpawn_FromInsideOnUpdate()
    {
        var spawnerHandle = _backend.SpawnEmpty(
            name: "Spawner",
            position: new Vec3(x: 1000f, y: 0f, z: 0f),
            parent: EntityHandle.None
        );
        var spawner = (WorldSpawnerComponent)_backend.AddComponent(
            entity: spawnerHandle,
            type: typeof(WorldSpawnerComponent)
        )!;

        GameWorld.Backend = _backend;
        WorldSpawnerComponent.PrefabPath = _prefabPath;
        try
        {
            _backend.BeginTick();
            _scripts.Update(
                root: _root,
                dt: 1f / 120f
            ); // spawner appends a child to _root mid-walk
            _backend.ApplyDeferred();

            Assert.True(spawner.Spawned.IsValid);
            Assert.True(_backend.IsAlive(spawner.Spawned));
            Assert.Contains(collection: _root.Children, filter: c => c.Name == "Bullet");
        }
        finally
        {
            GameWorld.Backend = null;
            WorldSpawnerComponent.PrefabPath = null;
        }
    }

    [Fact]
    public void Component_CanDestroyItself_FromInsideOnUpdate()
    {
        var h = _backend.SpawnEmpty(
            name: "Kamikaze",
            position: Vec3.Zero,
            parent: EntityHandle.None
        );
        _backend.AddComponent(entity: h, type: typeof(WorldSelfDestructComponent));

        GameWorld.Backend = _backend;
        try
        {
            _backend.BeginTick();
            _scripts.Update(root: _root, dt: 1f / 120f);
            Assert.True(_backend.IsAlive(h)); // deferred within the tick

            _backend.ApplyDeferred();
            Assert.False(_backend.IsAlive(h));
            Assert.DoesNotContain(collection: _root.Children, filter: c => c.Name == "Kamikaze");
        }
        finally
        {
            GameWorld.Backend = null;
        }
    }

    private sealed class RecordingHooks : IWorldSessionHooks
    {
        public readonly List<string> Destroying = [];
        public readonly List<string> Spawned = [];

        public void OnSpawned(SceneNode subtreeRoot) => Spawned.Add(subtreeRoot.Name);

        public void OnDestroying(SceneNode subtreeRoot) => Destroying.Add(subtreeRoot.Name);
    }
}
