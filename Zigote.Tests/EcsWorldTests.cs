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

    public void Dispose() => _w.Dispose();

    // ── Basic entity lifecycle ───────────────────────────────────────────────

    [Fact]
    public void CreateEntity_IsAlive_True()
    {
        var e = _w.CreateEntity();
        Assert.False(e.IsNull);
        Assert.True(_w.IsAlive(e));
        Assert.Equal(expected: 1, actual: _w.EntityCount);
    }

    [Fact]
    public void DestroyEntity_IsAlive_False()
    {
        var e = _w.CreateEntity();
        _w.DestroyEntity(e);
        Assert.False(_w.IsAlive(e));
        Assert.Equal(expected: 0, actual: _w.EntityCount);
    }

    [Fact]
    public void Entity_Null_IsAlive_False() => Assert.False(_w.IsAlive(Entity.Null));

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
            e: e,
            c: new Position {
                X = 1,
                Y = 2,
                Z = 3,
            }
        );
        Assert.True(_w.Has<Position>(e));
        Assert.Equal(expected: 2f, actual: _w.Get<Position>(e).Y);
    }

    [Fact]
    public void Get_Returns_Ref_Mutates_In_Place()
    {
        var e = _w.CreateEntity();
        _w.Set(e: e, c: new Position { Y = 7f });
        _w.Get<Position>(e).Y = 99f;
        Assert.Equal(expected: 99f, actual: _w.Get<Position>(e).Y);
    }

    [Fact]
    public void Add_Convenience_Overload_SetsValue()
    {
        var e = _w.CreateEntity();
        _w.Add(e: e, c: new Position { X = 5f });
        Assert.Equal(expected: 5f, actual: _w.Get<Position>(e).X);
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
        _w.Set(e: e, c: new Position { X = 1 });
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
        Assert.False(_w.TryGet<Position>(e: e, value: out _));
    }

    [Fact]
    public void TryGet_Returns_True_And_Value_When_Present()
    {
        var e = _w.CreateEntity();
        _w.Set(e: e, c: new Position { X = 42f });
        Assert.True(_w.TryGet<Position>(e: e, value: out var pos));
        Assert.Equal(expected: 42f, actual: pos.X);
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
        for (int i = 0; i < 100; i++)
            _w.Set(e: _w.CreateEntity(), c: new Position { X = i });

        float sum = 0f;
        _w.ForEach<Position>(span =>
            {
                foreach (ref var p in span) sum += p.X;
            }
        );
        Assert.Equal(expected: 4950f, actual: sum); // 0+1+…+99
    }

    [Fact]
    public void ForEach_TwoComponents_Visits_Intersection_Only()
    {
        var both = _w.CreateEntity();
        var posOnly = _w.CreateEntity();
        _w.Set(e: both, c: new Position { X = 1 });
        _w.Set(e: both, c: new Velocity { X = 10 });
        _w.Set(e: posOnly, c: new Position { X = 2 });

        int visited = 0;
        _w.ForEach<Position, Velocity>((pos, vel) =>
            {
                visited += pos.Length;
                for (int i = 0; i < pos.Length; i++) pos[i].X += vel[i].X;
            }
        );

        Assert.Equal(expected: 1, actual: visited);
        Assert.Equal(expected: 11f, actual: _w.Get<Position>(both).X);
        Assert.Equal(expected: 2f, actual: _w.Get<Position>(posOnly).X);
    }

    [Fact]
    public void Query_Span_Mutation_Persists()
    {
        for (int i = 0; i < 500; i++)
            _w.Set(e: _w.CreateEntity(), c: new Position { X = i });

        using var q = _w.Query<Position>();
        q.Each(span =>
            {
                foreach (ref var p in span) p.X *= 2f;
            }
        );

        float result = 0f;
        _w.ForEach<Position>(span =>
            {
                foreach (ref var p in span) result += p.X;
            }
        );
        Assert.Equal(expected: 499f * 500f, actual: result); // 2*(0+1+…+499)
    }

    // ── Systems pipeline ─────────────────────────────────────────────────────

    [Fact]
    public void RegisterSystem_Progress_Ticks_System()
    {
        for (int i = 0; i < 5; i++)
            _w.Set(e: _w.CreateEntity(), c: new Velocity { X = 1f });

        _w.RegisterSystem<Velocity>(
            name: "DoubleVelocity",
            phase: EcsPhase.OnUpdate,
            body: span =>
            {
                foreach (ref var v in span) v.X *= 2f;
            }
        );

        _w.Progress();

        float sum = 0f;
        _w.ForEach<Velocity>(span =>
            {
                foreach (ref var v in span) sum += v.X;
            }
        );
        Assert.Equal(expected: 10f, actual: sum); // 5 entities × (1*2)
    }

    [Fact]
    public void RegisterSystem_TwoComponents_Progress()
    {
        for (int i = 0; i < 4; i++)
        {
            var e = _w.CreateEntity();
            _w.Set(e: e, c: new Position { X = 0f });
            _w.Set(e: e, c: new Velocity { X = i + 1f });
        }

        _w.RegisterSystem<Position, Velocity>(
            name: "Move",
            phase: EcsPhase.OnUpdate,
            body: (pos, vel) =>
            {
                for (int i = 0; i < pos.Length; i++) pos[i].X += vel[i].X;
            }
        );

        _w.Progress();

        float sum = 0f;
        _w.ForEach<Position>(span =>
            {
                foreach (ref var p in span) sum += p.X;
            }
        );
        // velocities were 1,2,3,4 → sum of positions = 1+2+3+4 = 10
        Assert.Equal(expected: 10f, actual: sum);
    }

    // ── Deferred mutations ───────────────────────────────────────────────────

    [Fact]
    public void Defer_Allows_Structural_Changes_During_Iteration()
    {
        var e1 = _w.CreateEntity();
        var e2 = _w.CreateEntity();
        _w.Set(e: e1, c: new Position { X = 1 });
        _w.Set(e: e2, c: new Position { X = 2 });

        // Add Velocity inside a deferred block while iterating Position.
        _w.ForEach<Position>(span =>
            {
                _w.Defer(() => { _w.Set(e: e1, c: new Velocity { X = 9f }); });
            }
        );

        Assert.True(_w.Has<Velocity>(e1));
        Assert.Equal(expected: 9f, actual: _w.Get<Velocity>(e1).X);
    }

    // ── Hierarchy / relationships ────────────────────────────────────────────

    [Fact]
    public void SetParent_GetParent_RoundTrip()
    {
        var parent = _w.CreateEntity("Parent");
        var child = _w.CreateEntity("Child");
        _w.SetParent(child: child, parent: parent);
        var retrieved = _w.GetParent(child);
        Assert.Equal(expected: parent, actual: retrieved);
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
        _w.Set(e: prefab, c: new Position { X = 99f });

        var instance = _w.Instantiate(prefab);
        Assert.True(_w.IsAlive(instance));
        // flecs prefab instances inherit components via IsA — TryGet resolves via inheritance
        Assert.True(_w.TryGet<Position>(e: instance, value: out var pos));
        Assert.Equal(expected: 99f, actual: pos.X);
    }

    [Fact]
    public void IsA_Relationship()
    {
        var @base = _w.CreateEntity("Base");
        var derived = _w.CreateEntity("Derived");
        _w.IsA(e: derived, baseEntity: @base);
        // Relationship is set; query relations to verify (not exposed directly — just smoke)
        Assert.True(_w.IsAlive(derived));
        Assert.True(_w.IsAlive(@base));
    }

    // ── Large entity set ─────────────────────────────────────────────────────

    [Fact]
    public void Large_Entity_Set_Spans_Are_Correct()
    {
        for (int i = 0; i < 10_000; i++)
            _w.Set(e: _w.CreateEntity(), c: new Position { X = i });

        int count = 0;
        double sum = 0.0;
        _w.ForEach<Position>(span =>
            {
                count += span.Length;
                foreach (ref var p in span) sum += p.X;
            }
        );

        Assert.Equal(expected: 10_000, actual: count);
        Assert.Equal(expected: 10_000.0 * 9_999.0 / 2.0, actual: sum); // sum 0..9999
    }

    private struct Position
    {
        public float X, Y, Z;
    }

    private struct Velocity
    {
        public float X;
    }

    private struct Tag { }
}
