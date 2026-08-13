using Xunit;
using Zigote.Core.Math3D;
using Zigote.Ecs;
using Zigote.Scripting;
// Inside the Zigote.* namespace tree the bare name `World` binds to the Zigote.World namespace
// (enclosing-namespace members win over usings), so alias the provider class — game code in its
// own namespace (e.g. ExampleProject.Scripts) uses `World.Spawn(...)` directly.
using GameWorld = Zigote.Scripting.World;

namespace Zigote.Tests;

public class WorldProviderTests
{
    [Fact]
    public void Calls_Are_Safe_NoOps_Without_A_Backend()
    {
        GameWorld.Backend = null;
        Assert.False(GameWorld.IsAvailable);

        Assert.Equal(expected: EntityHandle.None, actual: GameWorld.Spawn("prefabs/tear.prefab"));
        Assert.Equal(expected: EntityHandle.None, actual: GameWorld.SpawnEmpty("Empty"));
        GameWorld.Destroy(new EntityHandle(3)); // must not throw
        Assert.False(GameWorld.IsAlive(new EntityHandle(3)));
        Assert.Equal(expected: Vec3.Zero, actual: GameWorld.GetPosition(new EntityHandle(3)));
        Assert.Equal(expected: Quat.Identity, actual: GameWorld.GetRotation(new EntityHandle(3)));
        Assert.Equal(expected: Vec3.One, actual: GameWorld.GetScale(new EntityHandle(3)));
        Assert.Null(GameWorld.GetName(new EntityHandle(3)));
        Assert.Equal(expected: EntityHandle.None, actual: GameWorld.Find("Player"));
        Assert.Equal(expected: 0, actual: GameWorld.CountByTag("Enemy"));
        Assert.Equal(expected: Entity.Null, actual: GameWorld.EcsEntity(new EntityHandle(3)));
        Assert.Null(GameWorld.FindComponent<Component>());

        var results = new List<EntityHandle> { new(9) };
        Assert.Equal(
            expected: 0,
            actual: GameWorld.OverlapSphere(center: Vec3.Zero, radius: 5f, results: results)
        );
        Assert.Empty(results); // cleared even without a backend
        results.Add(new EntityHandle(9));
        Assert.Equal(expected: 0, actual: GameWorld.FindAllByTag(tag: "Enemy", results: results));
        Assert.Empty(results);
    }

    [Fact]
    public void Spawn_Forwards_To_The_Backend()
    {
        var fake = new FakeWorldBackend();
        GameWorld.Backend = fake;
        try
        {
            var parent = new EntityHandle(7);
            var handle = GameWorld.Spawn(
                prefabPath: "prefabs/tear.prefab",
                position: new Vec3(x: 1f, y: 2f, z: 3f),
                rotation: Quat.Identity,
                parent: parent
            );

            Assert.Equal(expected: new EntityHandle(42), actual: handle);
            Assert.Equal(expected: "prefabs/tear.prefab", actual: fake.LastPrefabPath);
            Assert.Equal(expected: new Vec3(x: 1f, y: 2f, z: 3f), actual: fake.LastPosition);
            Assert.Equal(expected: parent, actual: fake.LastParent);
        }
        finally
        {
            GameWorld.Backend = null;
        }
    }

    [Fact]
    public void Spawn_Overloads_Default_To_Origin_Identity_Root()
    {
        var fake = new FakeWorldBackend();
        GameWorld.Backend = fake;
        try
        {
            GameWorld.Spawn("p.prefab");
            Assert.Equal(expected: Vec3.Zero, actual: fake.LastPosition);
            Assert.Equal(expected: Quat.Identity, actual: fake.LastRotation);
            Assert.Equal(expected: EntityHandle.None, actual: fake.LastParent);
        }
        finally
        {
            GameWorld.Backend = null;
        }
    }

    [Fact]
    public void Destroy_And_Queries_Forward()
    {
        var fake = new FakeWorldBackend();
        GameWorld.Backend = fake;
        try
        {
            GameWorld.Destroy(new EntityHandle(5));
            Assert.Equal(expected: new EntityHandle(5), actual: fake.LastDestroyed);

            GameWorld.SetTag(entity: new EntityHandle(5), tag: "Enemy");
            Assert.Equal(expected: "Enemy", actual: fake.LastTag);

            GameWorld.Nearest(center: new Vec3(x: 1f, y: 0f, z: 0f), maxRadius: 10f, tag: "Enemy");
            Assert.Equal(expected: "Enemy", actual: fake.LastNearestTag);
            Assert.Equal(expected: EntityHandle.None, actual: fake.LastNearestIgnore);
        }
        finally
        {
            GameWorld.Backend = null;
        }
    }

    [Fact]
    public void Of_Wraps_A_Components_EntityId()
    {
        var comp = new ProviderProbeComponent();
        Assert.Equal(
            expected: EntityHandle.None,
            actual: GameWorld.Of(comp)
        ); // unattached → id 0 → None
    }

    [Fact]
    public void EntityHandle_Equality_And_Sentinel()
    {
        Assert.Equal(expected: EntityHandle.None, actual: default);
        Assert.False(default(EntityHandle).IsValid);
        Assert.True(new EntityHandle(1).IsValid);
        Assert.True(new EntityHandle(3) == new EntityHandle(3));
        Assert.True(new EntityHandle(3) != new EntityHandle(4));
    }

    private sealed class ProviderProbeComponent : Component;

    private sealed class FakeWorldBackend : IWorldBackend
    {
        public EntityHandle LastDestroyed;
        public EntityHandle LastNearestIgnore;
        public string? LastNearestTag;
        public EntityHandle LastParent;
        public Vec3 LastPosition;
        public string? LastPrefabPath;
        public Quat LastRotation;
        public string? LastTag;

        public EntityHandle Spawn(string prefabPath, Vec3 position, Quat rotation,
            EntityHandle parent)
        {
            LastPrefabPath = prefabPath;
            LastPosition = position;
            LastRotation = rotation;
            LastParent = parent;
            return new EntityHandle(42);
        }

        public EntityHandle SpawnEmpty(string name, Vec3 position, EntityHandle parent) => new(43);

        public void Destroy(EntityHandle entity) => LastDestroyed = entity;

        public bool IsAlive(EntityHandle entity) => false;

        public Vec3 GetPosition(EntityHandle entity) => Vec3.Zero;

        public void SetPosition(EntityHandle entity, Vec3 position) { }

        public Quat GetRotation(EntityHandle entity) => Quat.Identity;

        public void SetRotation(EntityHandle entity, Quat rotation) { }

        public Vec3 GetScale(EntityHandle entity) => Vec3.One;

        public void SetScale(EntityHandle entity, Vec3 scale) { }

        public Vec3 GetWorldPosition(EntityHandle entity) => Vec3.Zero;

        public bool GetVisible(EntityHandle entity) => true;

        public void SetVisible(EntityHandle entity, bool visible) { }

        public string? GetName(EntityHandle entity) => null;

        public string? GetTag(EntityHandle entity) => LastTag;

        public void SetTag(EntityHandle entity, string? tag) => LastTag = tag;

        public EntityHandle GetParent(EntityHandle entity) => EntityHandle.None;

        public void SetParent(EntityHandle child, EntityHandle parent) { }

        public EntityHandle Find(string name) => EntityHandle.None;

        public int FindAllByTag(string tag, List<EntityHandle> results)
        {
            results.Clear();
            return 0;
        }

        public int CountByTag(string tag) => 0;

        public int OverlapSphere(Vec3 center, float radius, List<EntityHandle> results, string? tag)
        {
            results.Clear();
            return 0;
        }

        public EntityHandle Nearest(Vec3 center, float maxRadius, string? tag, EntityHandle ignore)
        {
            LastNearestTag = tag;
            LastNearestIgnore = ignore;
            return EntityHandle.None;
        }

        public Component? GetComponent(EntityHandle entity, Type type) => null;

        public Component? AddComponent(EntityHandle entity, Type type) => null;

        public Component? FindComponent(Type type) => null;

        public Entity EcsEntity(EntityHandle entity) => Entity.Null;
    }
}
