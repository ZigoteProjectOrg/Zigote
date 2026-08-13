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

    protected override void OnCreate()
    {
        Created = true;
    }

    protected override void OnUpdate(float dt)
    {
        Updates++;
    }

    protected override void OnDestroy()
    {
        Destroyed = true;
    }
}

/// <summary>Spawns one prefab from inside OnUpdate — exercises mid-walk tree mutation.</summary>
public sealed class WorldSpawnerComponent : Component
{
    public static string? PrefabPath;
    public EntityHandle Spawned;

    protected override void OnUpdate(float dt)
    {
        if (!Spawned.IsValid && PrefabPath != null)
            Spawned = GameWorld.Spawn(PrefabPath, new Vec3(5f, 0f, 0f));
    }
}

/// <summary>Destroys its own entity from inside OnUpdate — exercises the deferred-destroy path.</summary>
public sealed class WorldSelfDestructComponent : Component
{
    protected override void OnUpdate(float dt)
    {
        GameWorld.Destroy(GameWorld.Of(this));
    }
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
                Position = new Vec3(0f, 0f, 0f),
            }
        );
        _root.AddChild(
            new SceneNode("Rock") {
                Tag = "Obstacle",
                Position = new Vec3(10f, 0f, 0f),
            }
        );

        var registry = new ScriptRegistry();
        registry.Load(typeof(WorldRuntimeTests).Assembly);
        _scripts = new ScriptWorld(registry);

        _ecs = new EcsSceneBridge();
        _ecs.BuildFrom(_root); // same order as GameSession: bridge first, then the backend
        _backend = new RuntimeWorldBackend(
            _root,
            _scripts,
            _ecs,
            _hooks
        );

        _prefabPath = Path.Combine(_dir, "bullet.prefab");
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
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
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
            _prefabPath,
            new Vec3(1f, 2f, 3f),
            Quat.Identity,
            EntityHandle.None
        );

        Assert.True(h.IsValid);
        Assert.True(_backend.IsAlive(h));
        Assert.Equal("Bullet", _backend.GetName(h));
        Assert.Equal(new Vec3(1f, 2f, 3f), _backend.GetPosition(h));

        // In the tree, under the root, with its child intact
        var node = _root.Children.Single(c => c.Name == "Bullet");
        Assert.Single(node.Children); // "Fin"

        // Scripts attached and OnCreate ran inside Spawn
        var probe =
            Assert.IsType<WorldProbeComponent>(
                _backend.GetComponent(h, typeof(WorldProbeComponent))
            );
        Assert.True(probe.Created);

        // Session hooks saw the subtree
        Assert.Equal(["Bullet"], _hooks.Spawned);

        // flecs mirror: a live entity carrying the canonical Transform, parented under the root's entity
        var e = _backend.EcsEntity(h);
        Assert.False(e.IsNull);
        Assert.True(_ecs.World.IsAlive(e));
        Assert.Equal(_ecs.EntityOf(_root.Id), _ecs.World.GetParent(e));

        // Template tag seeded the index
        Assert.Equal(1, _backend.CountByTag("Projectile"));
    }

    [Fact]
    public void Spawn_UnderParent_ParentsAndBakesWorldPosition()
    {
        var parent = _backend.Find("Player");
        _backend.SetPosition(parent, new Vec3(100f, 0f, 0f));

        var h = _backend.Spawn(
            _prefabPath,
            new Vec3(1f, 0f, 0f),
            Quat.Identity,
            parent
        );

        Assert.Equal(parent, _backend.GetParent(h));
        Assert.Equal(new Vec3(1f, 0f, 0f), _backend.GetPosition(h)); // local
        Assert.Equal(new Vec3(101f, 0f, 0f), _backend.GetWorldPosition(h)); // parent-baked
    }

    [Fact]
    public void Spawn_MissingPrefab_ReturnsNone()
    {
        Assert.Equal(
            EntityHandle.None,
            _backend.Spawn(
                Path.Combine(_dir, "nope.prefab"),
                Vec3.Zero,
                Quat.Identity,
                EntityHandle.None
            )
        );
    }

    [Fact]
    public void Spawn_UnderDeadParent_ReturnsNone()
    {
        var h = _backend.SpawnEmpty("Mount", Vec3.Zero, EntityHandle.None);
        _backend.Destroy(h);
        Tick(_backend);

        Assert.Equal(
            EntityHandle.None,
            _backend.Spawn(
                _prefabPath,
                Vec3.Zero,
                Quat.Identity,
                h
            )
        );
    }

    [Fact]
    public void SpawnEmpty_Plus_AddComponent()
    {
        var h = _backend.SpawnEmpty("Mount", new Vec3(2f, 0f, 0f), EntityHandle.None);

        var comp = _backend.AddComponent(h, typeof(WorldProbeComponent));
        var probe = Assert.IsType<WorldProbeComponent>(comp);
        Assert.True(probe.Created);
        Assert.Same(probe, _backend.GetComponent(h, typeof(WorldProbeComponent)));
    }

    // ── Destroy (deferred) ────────────────────────────────────────────────────

    [Fact]
    public void Destroy_IsDeferred_ThenTearsEverythingDown()
    {
        var h = _backend.Spawn(
            _prefabPath,
            Vec3.Zero,
            Quat.Identity,
            EntityHandle.None
        );
        var probe = (WorldProbeComponent)_backend.GetComponent(h, typeof(WorldProbeComponent))!;
        var e = _backend.EcsEntity(h);

        _backend.Destroy(h);
        Assert.True(_backend.IsAlive(h)); // handles stay valid until the tick ends

        Tick(_backend);

        Assert.False(_backend.IsAlive(h));
        Assert.True(probe.Destroyed);
        Assert.DoesNotContain(_root.Children, c => c.Name == "Bullet");
        Assert.Equal(["Bullet"], _hooks.Destroying);
        Assert.False(_ecs.World.IsAlive(e));
        Assert.Equal(0, _backend.CountByTag("Projectile"));
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
            _prefabPath,
            Vec3.Zero,
            Quat.Identity,
            EntityHandle.None
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
        var authoredCount = _root.Children.Count;
        _backend.Spawn(
            _prefabPath,
            Vec3.Zero,
            Quat.Identity,
            EntityHandle.None
        );
        _backend.Spawn(
            _prefabPath,
            Vec3.Zero,
            Quat.Identity,
            EntityHandle.None
        );

        _backend.RestoreSceneEdits();

        Assert.Equal(authoredCount, _root.Children.Count);
        Assert.DoesNotContain(_root.Children, c => c.Name == "Bullet");
    }

    [Fact]
    public void RestoreSceneEdits_ReattachesADestroyedAuthoredNode()
    {
        var rock = _backend.Find("Rock");
        _backend.Destroy(rock);
        Tick(_backend);
        Assert.DoesNotContain(_root.Children, c => c.Name == "Rock");

        _backend.RestoreSceneEdits();

        Assert.Equal("Rock", _root.Children[1].Name); // back at its original index
        Assert.Same(_root, _root.Children[1].Parent);
    }

    [Fact]
    public void RestoreSceneEdits_ReattachesSpawnNestedUnderDestroyedAuthored()
    {
        var rock = _backend.Find("Rock");
        var spawned = _backend.Spawn(
            _prefabPath,
            Vec3.Zero,
            Quat.Identity,
            rock
        );
        Assert.True(spawned.IsValid);

        _backend.Destroy(rock);
        Tick(_backend);
        Assert.False(_backend.IsAlive(spawned)); // died with its parent, with full teardown
        Assert.Contains("Bullet", _hooks.Destroying);

        _backend.RestoreSceneEdits();
        var restored = _root.Children.Single(c => c.Name == "Rock");
        Assert.DoesNotContain(restored.Children, c => c.Name == "Bullet"); // spawn not resurrected
    }

    [Fact]
    public void RestoreSceneEdits_RestoresVisibility()
    {
        var rock = _backend.Find("Rock");
        _backend.SetVisible(rock, false);
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

        _backend.SetParent(rock, player);
        Assert.Equal(new EntityHandle((uint)_root.Id), _backend.GetParent(rock)); // not yet

        Tick(_backend);
        Assert.Equal(player, _backend.GetParent(rock));

        _backend.RestoreSceneEdits();
        Assert.Equal("Rock", _root.Children[1].Name); // authored structure back
    }

    [Fact]
    public void SetParent_RefusesCycles()
    {
        var player = _backend.Find("Player");
        var child = _backend.Spawn(
            _prefabPath,
            Vec3.Zero,
            Quat.Identity,
            player
        );

        _backend.SetParent(player, child);
        Tick(_backend);

        Assert.Equal(new EntityHandle((uint)_root.Id), _backend.GetParent(player));
    }

    // ── Find / tags / spatial ─────────────────────────────────────────────────

    [Fact]
    public void Find_ReturnsFirstLiveMatchInTreeOrder()
    {
        Assert.Equal("Player", _backend.GetName(_backend.Find("Player")));
        Assert.Equal(EntityHandle.None, _backend.Find("Ghost"));
    }

    [Fact]
    public void Tags_SeededFromAuthoredNodes_AndSessionRetaggable()
    {
        Assert.Equal(1, _backend.CountByTag("Obstacle"));

        var rock = _backend.Find("Rock");
        _backend.SetTag(rock, "Rubble");
        Assert.Equal(0, _backend.CountByTag("Obstacle"));
        Assert.Equal("Rubble", _backend.GetTag(rock));

        // Session-local: the authored node's Tag was never touched
        Assert.Equal("Obstacle", _root.Children.Single(c => c.Name == "Rock").Tag);

        var results = new List<EntityHandle>();
        Assert.Equal(1, _backend.FindAllByTag("Rubble", results));
        Assert.Equal(rock, results[0]);
    }

    [Fact]
    public void OverlapSphere_UsesWorldPositions_AndTagFilter()
    {
        _backend.BeginTick();
        var results = new List<EntityHandle>();

        // Player at 0, Rock at 10 — radius 5 around origin hits only the Player
        Assert.Equal(
            1,
            _backend.OverlapSphere(
                Vec3.Zero,
                5f,
                results,
                null
            )
        );
        Assert.Equal(_backend.Find("Player"), results[0]);

        // Tag filter
        Assert.Equal(
            0,
            _backend.OverlapSphere(
                Vec3.Zero,
                5f,
                results,
                "Obstacle"
            )
        );
        Assert.Equal(
            1,
            _backend.OverlapSphere(
                Vec3.Zero,
                50f,
                results,
                "Obstacle"
            )
        );

        // Position writes invalidate the per-tick index
        _backend.SetPosition(_backend.Find("Rock"), new Vec3(1f, 0f, 0f));
        Assert.Equal(
            1,
            _backend.OverlapSphere(
                Vec3.Zero,
                5f,
                results,
                "Obstacle"
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
            player,
            _backend.Nearest(
                new Vec3(1f, 0f, 0f),
                100f,
                null,
                EntityHandle.None
            )
        );
        Assert.Equal(
            rock,
            _backend.Nearest(
                new Vec3(1f, 0f, 0f),
                100f,
                null,
                player
            )
        );
        Assert.Equal(
            rock,
            _backend.Nearest(
                new Vec3(1f, 0f, 0f),
                100f,
                "Obstacle",
                EntityHandle.None
            )
        );
        Assert.Equal(
            EntityHandle.None,
            _backend.Nearest(
                new Vec3(1f, 0f, 0f),
                2f,
                "Obstacle",
                EntityHandle.None
            )
        );
    }

    [Fact]
    public void FindComponent_ScansTheTree()
    {
        Assert.Null(_backend.FindComponent(typeof(WorldProbeComponent)));
        _backend.Spawn(
            _prefabPath,
            Vec3.Zero,
            Quat.Identity,
            EntityHandle.None
        );
        Assert.IsType<WorldProbeComponent>(_backend.FindComponent(typeof(WorldProbeComponent)));
    }

    // ── Scripts driving the API mid-tick (provider wired, like play mode) ─────

    [Fact]
    public void Component_CanSpawn_FromInsideOnUpdate()
    {
        var spawnerHandle = _backend.SpawnEmpty(
            "Spawner",
            new Vec3(1000f, 0f, 0f),
            EntityHandle.None
        );
        var spawner = (WorldSpawnerComponent)_backend.AddComponent(
            spawnerHandle,
            typeof(WorldSpawnerComponent)
        )!;

        GameWorld.Backend = _backend;
        WorldSpawnerComponent.PrefabPath = _prefabPath;
        try
        {
            _backend.BeginTick();
            _scripts.Update(_root, 1f / 120f); // spawner appends a child to _root mid-walk
            _backend.ApplyDeferred();

            Assert.True(spawner.Spawned.IsValid);
            Assert.True(_backend.IsAlive(spawner.Spawned));
            Assert.Contains(_root.Children, c => c.Name == "Bullet");
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
        var h = _backend.SpawnEmpty("Kamikaze", Vec3.Zero, EntityHandle.None);
        _backend.AddComponent(h, typeof(WorldSelfDestructComponent));

        GameWorld.Backend = _backend;
        try
        {
            _backend.BeginTick();
            _scripts.Update(_root, 1f / 120f);
            Assert.True(_backend.IsAlive(h)); // deferred within the tick

            _backend.ApplyDeferred();
            Assert.False(_backend.IsAlive(h));
            Assert.DoesNotContain(_root.Children, c => c.Name == "Kamikaze");
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

        public void OnSpawned(SceneNode subtreeRoot)
        {
            Spawned.Add(subtreeRoot.Name);
        }

        public void OnDestroying(SceneNode subtreeRoot)
        {
            Destroying.Add(subtreeRoot.Name);
        }
    }
}
