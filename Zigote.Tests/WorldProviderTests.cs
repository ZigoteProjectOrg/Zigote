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

        Assert.Equal(EntityHandle.None, GameWorld.Spawn("prefabs/tear.prefab"));
        Assert.Equal(EntityHandle.None, GameWorld.SpawnEmpty("Empty"));
        GameWorld.Destroy(new EntityHandle(3)); // must not throw
        Assert.False(GameWorld.IsAlive(new EntityHandle(3)));
        Assert.Equal(Vec3.Zero, GameWorld.GetPosition(new EntityHandle(3)));
        Assert.Equal(Quat.Identity, GameWorld.GetRotation(new EntityHandle(3)));
        Assert.Equal(Vec3.One, GameWorld.GetScale(new EntityHandle(3)));
        Assert.Null(GameWorld.GetName(new EntityHandle(3)));
        Assert.Equal(EntityHandle.None, GameWorld.Find("Player"));
        Assert.Equal(0, GameWorld.CountByTag("Enemy"));
        Assert.Equal(Entity.Null, GameWorld.EcsEntity(new EntityHandle(3)));
        Assert.Null(GameWorld.FindComponent<Component>());

        var results = new List<EntityHandle> { new(9) };
        Assert.Equal(0, GameWorld.OverlapSphere(Vec3.Zero, 5f, results));
        Assert.Empty(results); // cleared even without a backend
        results.Add(new EntityHandle(9));
        Assert.Equal(0, GameWorld.FindAllByTag("Enemy", results));
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
                "prefabs/tear.prefab",
                new Vec3(1f, 2f, 3f),
                Quat.Identity,
                parent
            );

            Assert.Equal(new EntityHandle(42), handle);
            Assert.Equal("prefabs/tear.prefab", fake.LastPrefabPath);
            Assert.Equal(new Vec3(1f, 2f, 3f), fake.LastPosition);
            Assert.Equal(parent, fake.LastParent);
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
            Assert.Equal(Vec3.Zero, fake.LastPosition);
            Assert.Equal(Quat.Identity, fake.LastRotation);
            Assert.Equal(EntityHandle.None, fake.LastParent);
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
            Assert.Equal(new EntityHandle(5), fake.LastDestroyed);

            GameWorld.SetTag(new EntityHandle(5), "Enemy");
            Assert.Equal("Enemy", fake.LastTag);

            GameWorld.Nearest(new Vec3(1f, 0f, 0f), 10f, "Enemy");
            Assert.Equal("Enemy", fake.LastNearestTag);
            Assert.Equal(EntityHandle.None, fake.LastNearestIgnore);
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
        Assert.Equal(EntityHandle.None, GameWorld.Of(comp)); // unattached → id 0 → None
    }

    [Fact]
    public void EntityHandle_Equality_And_Sentinel()
    {
        Assert.Equal(EntityHandle.None, default);
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

        public EntityHandle SpawnEmpty(string name, Vec3 position, EntityHandle parent)
        {
            return new EntityHandle(43);
        }

        public void Destroy(EntityHandle entity)
        {
            LastDestroyed = entity;
        }

        public bool IsAlive(EntityHandle entity)
        {
            return false;
        }

        public Vec3 GetPosition(EntityHandle entity)
        {
            return Vec3.Zero;
        }

        public void SetPosition(EntityHandle entity, Vec3 position)
        {
        }

        public Quat GetRotation(EntityHandle entity)
        {
            return Quat.Identity;
        }

        public void SetRotation(EntityHandle entity, Quat rotation)
        {
        }

        public Vec3 GetScale(EntityHandle entity)
        {
            return Vec3.One;
        }

        public void SetScale(EntityHandle entity, Vec3 scale)
        {
        }

        public Vec3 GetWorldPosition(EntityHandle entity)
        {
            return Vec3.Zero;
        }

        public bool GetVisible(EntityHandle entity)
        {
            return true;
        }

        public void SetVisible(EntityHandle entity, bool visible)
        {
        }

        public string? GetName(EntityHandle entity)
        {
            return null;
        }

        public string? GetTag(EntityHandle entity)
        {
            return LastTag;
        }

        public void SetTag(EntityHandle entity, string? tag)
        {
            LastTag = tag;
        }

        public EntityHandle GetParent(EntityHandle entity)
        {
            return EntityHandle.None;
        }

        public void SetParent(EntityHandle child, EntityHandle parent)
        {
        }

        public EntityHandle Find(string name)
        {
            return EntityHandle.None;
        }

        public int FindAllByTag(string tag, List<EntityHandle> results)
        {
            results.Clear();
            return 0;
        }

        public int CountByTag(string tag)
        {
            return 0;
        }

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

        public Component? GetComponent(EntityHandle entity, Type type)
        {
            return null;
        }

        public Component? AddComponent(EntityHandle entity, Type type)
        {
            return null;
        }

        public Component? FindComponent(Type type)
        {
            return null;
        }

        public Entity EcsEntity(EntityHandle entity)
        {
            return Entity.Null;
        }
    }
}
