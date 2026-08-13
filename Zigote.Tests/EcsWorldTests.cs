using Xunit;
using Zigote.Ecs;

namespace Zigote.Tests;

/// <summary>
///     Integration tests for <see cref="EcsWorld" /> (flecs backend).
///     These tests require libzigote to be available at runtime (built by the Zig pre-build step).
/// </summary>
public class EcsWorldTests : IDisposable
{
    private readonly EcsWorld _w = new();

    public void Dispose()
    {
        _w.Dispose();
    }

    // ── Basic entity lifecycle ───────────────────────────────────────────────

    [Fact]
    public void CreateEntity_IsAlive_True()
    {
        var e = _w.CreateEntity();
        Assert.False(e.IsNull);
        Assert.True(_w.IsAlive(e));
        Assert.Equal(1, _w.EntityCount);
    }

    [Fact]
    public void DestroyEntity_IsAlive_False()
    {
        var e = _w.CreateEntity();
        _w.DestroyEntity(e);
        Assert.False(_w.IsAlive(e));
        Assert.Equal(0, _w.EntityCount);
    }

    [Fact]
    public void Entity_Null_IsAlive_False()
    {
        Assert.False(_w.IsAlive(Entity.Null));
    }

    [Fact]
    public void NullEntity_IsNull_True()
    {
        Assert.True(Entity.Null.IsNull);
        var e = _w.CreateEntity();
        Assert.False(e.IsNull);
    }

    // ── Component CRUD ───────────────────────────────────────────────────────

    [Fact]
    public void Set_Has_Get()
    {
        var e = _w.CreateEntity();
        Assert.False(_w.Has<Position>(e));

        _w.Set(
            e,
            new Position {
                X = 1,
                Y = 2,
                Z = 3,
            }
        );
        Assert.True(_w.Has<Position>(e));
        Assert.Equal(2f, _w.Get<Position>(e).Y);
    }

    [Fact]
    public void Get_Returns_Ref_Mutates_In_Place()
    {
        var e = _w.CreateEntity();
        _w.Set(e, new Position { Y = 7f });
        _w.Get<Position>(e).Y = 99f;
        Assert.Equal(99f, _w.Get<Position>(e).Y);
    }

    [Fact]
    public void Add_Convenience_Overload_SetsValue()
    {
        var e = _w.CreateEntity();
        _w.Add(e, new Position { X = 5f });
        Assert.Equal(5f, _w.Get<Position>(e).X);
    }

    [Fact]
    public void Has_False_Before_Set()
    {
        var e = _w.CreateEntity();
        Assert.False(_w.Has<Velocity>(e));
    }

    [Fact]
    public void Remove_Returns_True_When_Present()
    {
        var e = _w.CreateEntity();
        _w.Set(e, new Position { X = 1 });
        Assert.True(_w.Remove<Position>(e));
        Assert.False(_w.Has<Position>(e));
    }

    [Fact]
    public void Remove_Returns_False_When_Absent()
    {
        var e = _w.CreateEntity();
        Assert.False(_w.Remove<Position>(e));
    }

    [Fact]
    public void TryGet_Returns_False_For_Missing_Component()
    {
        var e = _w.CreateEntity();
        Assert.False(_w.TryGet<Position>(e, out _));
    }

    [Fact]
    public void TryGet_Returns_True_And_Value_When_Present()
    {
        var e = _w.CreateEntity();
        _w.Set(e, new Position { X = 42f });
        Assert.True(_w.TryGet<Position>(e, out var pos));
        Assert.Equal(42f, pos.X);
    }

    [Fact]
    public void Get_Throws_When_Component_Absent()
    {
        var e = _w.CreateEntity();
        Assert.Throws<InvalidOperationException>(() => _w.Get<Position>(e));
    }

    // ── Query iteration ──────────────────────────────────────────────────────

    [Fact]
    public void ForEach_SingleComponent_Visits_All()
    {
        for (var i = 0; i < 100; i++)
            _w.Set(_w.CreateEntity(), new Position { X = i });

        var sum = 0f;
        _w.ForEach<Position>(span =>
            {
                foreach (ref var p in span) sum += p.X;
            }
        );
        Assert.Equal(4950f, sum); // 0+1+…+99
    }

    [Fact]
    public void ForEach_TwoComponents_Visits_Intersection_Only()
    {
        var both = _w.CreateEntity();
        var posOnly = _w.CreateEntity();
        _w.Set(both, new Position { X = 1 });
        _w.Set(both, new Velocity { X = 10 });
        _w.Set(posOnly, new Position { X = 2 });

        var visited = 0;
        _w.ForEach<Position, Velocity>((pos, vel) =>
            {
                visited += pos.Length;
                for (var i = 0; i < pos.Length; i++) pos[i].X += vel[i].X;
            }
        );

        Assert.Equal(1, visited);
        Assert.Equal(11f, _w.Get<Position>(both).X);
        Assert.Equal(2f, _w.Get<Position>(posOnly).X);
    }

    [Fact]
    public void Query_Span_Mutation_Persists()
    {
        for (var i = 0; i < 500; i++)
            _w.Set(_w.CreateEntity(), new Position { X = i });

        using var q = _w.Query<Position>();
        q.Each(span =>
            {
                foreach (ref var p in span) p.X *= 2f;
            }
        );

        var result = 0f;
        _w.ForEach<Position>(span =>
            {
                foreach (ref var p in span) result += p.X;
            }
        );
        Assert.Equal(499f * 500f, result); // 2*(0+1+…+499)
    }

    // ── Systems pipeline ─────────────────────────────────────────────────────

    [Fact]
    public void RegisterSystem_Progress_Ticks_System()
    {
        for (var i = 0; i < 5; i++)
            _w.Set(_w.CreateEntity(), new Velocity { X = 1f });

        _w.RegisterSystem<Velocity>(
            "DoubleVelocity",
            EcsPhase.OnUpdate,
            span =>
            {
                foreach (ref var v in span) v.X *= 2f;
            }
        );

        _w.Progress();

        var sum = 0f;
        _w.ForEach<Velocity>(span =>
            {
                foreach (ref var v in span) sum += v.X;
            }
        );
        Assert.Equal(10f, sum); // 5 entities × (1*2)
    }

    [Fact]
    public void RegisterSystem_TwoComponents_Progress()
    {
        for (var i = 0; i < 4; i++)
        {
            var e = _w.CreateEntity();
            _w.Set(e, new Position { X = 0f });
            _w.Set(e, new Velocity { X = i + 1f });
        }

        _w.RegisterSystem<Position, Velocity>(
            "Move",
            EcsPhase.OnUpdate,
            (pos, vel) =>
            {
                for (var i = 0; i < pos.Length; i++) pos[i].X += vel[i].X;
            }
        );

        _w.Progress();

        var sum = 0f;
        _w.ForEach<Position>(span =>
            {
                foreach (ref var p in span) sum += p.X;
            }
        );
        // velocities were 1,2,3,4 → sum of positions = 1+2+3+4 = 10
        Assert.Equal(10f, sum);
    }

    // ── Deferred mutations ───────────────────────────────────────────────────

    [Fact]
    public void Defer_Allows_Structural_Changes_During_Iteration()
    {
        var e1 = _w.CreateEntity();
        var e2 = _w.CreateEntity();
        _w.Set(e1, new Position { X = 1 });
        _w.Set(e2, new Position { X = 2 });

        // Add Velocity inside a deferred block while iterating Position.
        _w.ForEach<Position>(span => { _w.Defer(() => { _w.Set(e1, new Velocity { X = 9f }); }); });

        Assert.True(_w.Has<Velocity>(e1));
        Assert.Equal(9f, _w.Get<Velocity>(e1).X);
    }

    // ── Hierarchy / relationships ────────────────────────────────────────────

    [Fact]
    public void SetParent_GetParent_RoundTrip()
    {
        var parent = _w.CreateEntity("Parent");
        var child = _w.CreateEntity("Child");
        _w.SetParent(child, parent);
        var retrieved = _w.GetParent(child);
        Assert.Equal(parent, retrieved);
    }

    [Fact]
    public void SetParent_Zero_Returns_ZeroParent_When_No_Parent()
    {
        var e = _w.CreateEntity();
        var p = _w.GetParent(e);
        Assert.True(p.IsNull);
    }

    // ── Prefabs / IsA ────────────────────────────────────────────────────────

    [Fact]
    public void Prefab_Instantiate_Inherits_Components()
    {
        var prefab = _w.NewPrefab("Bullet");
        _w.Set(prefab, new Position { X = 99f });

        var instance = _w.Instantiate(prefab);
        Assert.True(_w.IsAlive(instance));
        // flecs prefab instances inherit components via IsA — TryGet resolves via inheritance
        Assert.True(_w.TryGet<Position>(instance, out var pos));
        Assert.Equal(99f, pos.X);
    }

    [Fact]
    public void IsA_Relationship()
    {
        var @base = _w.CreateEntity("Base");
        var derived = _w.CreateEntity("Derived");
        _w.IsA(derived, @base);
        // Relationship is set; query relations to verify (not exposed directly — just smoke)
        Assert.True(_w.IsAlive(derived));
        Assert.True(_w.IsAlive(@base));
    }

    // ── Large entity set ─────────────────────────────────────────────────────

    [Fact]
    public void Large_Entity_Set_Spans_Are_Correct()
    {
        for (var i = 0; i < 10_000; i++)
            _w.Set(_w.CreateEntity(), new Position { X = i });

        var count = 0;
        var sum = 0.0;
        _w.ForEach<Position>(span =>
            {
                count += span.Length;
                foreach (ref var p in span) sum += p.X;
            }
        );

        Assert.Equal(10_000, count);
        Assert.Equal(10_000.0 * 9_999.0 / 2.0, sum); // sum 0..9999
    }

    private struct Position
    {
        public float X, Y, Z;
    }

    private struct Velocity
    {
        public float X;
    }

    private struct Tag
    {
    }
}
