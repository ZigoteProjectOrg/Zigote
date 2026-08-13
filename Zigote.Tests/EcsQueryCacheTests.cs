using Xunit;
using Zigote.Ecs;

namespace Zigote.Tests;

/// <summary>
///     Covers the <see cref="EcsWorld.ForEach{T1}" /> query cache, the 3/4-component
///     query/system overloads, and iterator cleanup when a query callback throws.
///     These tests require libzigote to be available at runtime (built by the Zig pre-build step).
/// </summary>
public class EcsQueryCacheTests : IDisposable
{
    private readonly EcsWorld _w = new();

    public void Dispose()
    {
        _w.Dispose();
    }

    // ── ForEach query cache ──────────────────────────────────────────────────

    [Fact]
    public void ForEach_Repeated_Calls_Stay_Correct()
    {
        for (var i = 0; i < 10; i++)
            _w.Set(_w.CreateEntity(), new A { X = 1f });

        var total = 0f;
        for (var frame = 0; frame < 100; frame++)
            _w.ForEach<A>(span =>
                {
                    foreach (ref var a in span) total += a.X;
                }
            );
        Assert.Equal(1000f, total);
    }

    [Fact]
    public void ForEach_Then_Dispose_Destroys_Cached_Queries()
    {
        _w.Set(_w.CreateEntity(), new A { X = 1f });
        _w.ForEach<A>(span => { });
        _w.ForEach<A, B>((a, b) => { });
        _w.Dispose();
    }

    [Fact]
    public void ForEach_Sees_Entities_Created_After_First_Call()
    {
        _w.Set(_w.CreateEntity(), new A { X = 1f });
        var first = 0;
        _w.ForEach<A>(span => { first += span.Length; });
        Assert.Equal(1, first);

        _w.Set(_w.CreateEntity(), new A { X = 2f });
        var second = 0;
        _w.ForEach<A>(span => { second += span.Length; });
        Assert.Equal(2, second);
    }

    // ── 3/4-component overloads ──────────────────────────────────────────────

    [Fact]
    public void ForEach_ThreeComponents_Visits_Intersection_Only()
    {
        var all = _w.CreateEntity();
        _w.Set(all, new A { X = 1 });
        _w.Set(all, new B { X = 10 });
        _w.Set(all, new C { X = 100 });
        var partial = _w.CreateEntity();
        _w.Set(partial, new A { X = 2 });
        _w.Set(partial, new B { X = 20 });

        var visited = 0;
        var sum = 0f;
        _w.ForEach<A, B, C>((a, b, c) =>
            {
                visited += a.Length;
                for (var i = 0; i < a.Length; i++) sum += a[i].X + b[i].X + c[i].X;
            }
        );

        Assert.Equal(1, visited);
        Assert.Equal(111f, sum);
    }

    [Fact]
    public void ForEach_FourComponents_Visits_Intersection_Only()
    {
        var all = _w.CreateEntity();
        _w.Set(all, new A { X = 1 });
        _w.Set(all, new B { X = 10 });
        _w.Set(all, new C { X = 100 });
        _w.Set(all, new D { X = 1000 });
        var partial = _w.CreateEntity();
        _w.Set(partial, new A { X = 2 });
        _w.Set(partial, new B { X = 20 });
        _w.Set(partial, new C { X = 200 });

        var visited = 0;
        var sum = 0f;
        _w.ForEach<A, B, C, D>((a, b, c, d) =>
            {
                visited += a.Length;
                for (var i = 0; i < a.Length; i++) sum += a[i].X + b[i].X + c[i].X + d[i].X;
            }
        );

        Assert.Equal(1, visited);
        Assert.Equal(1111f, sum);
    }

    [Fact]
    public void Query_ThreeComponents_Span_Mutation_Persists()
    {
        var e = _w.CreateEntity();
        _w.Set(e, new A { X = 1f });
        _w.Set(e, new B { X = 2f });
        _w.Set(e, new C { X = 3f });

        using var q = _w.Query<A, B, C>();
        q.Each((a, b, c) =>
            {
                for (var i = 0; i < a.Length; i++) a[i].X += b[i].X * c[i].X;
            }
        );

        Assert.Equal(7f, _w.Get<A>(e).X);
    }

    [Fact]
    public void Query_FourComponents_Entity_Overload_Reports_Entities()
    {
        var e = _w.CreateEntity();
        _w.Set(e, new A { X = 1f });
        _w.Set(e, new B { X = 2f });
        _w.Set(e, new C { X = 3f });
        _w.Set(e, new D { X = 4f });

        using var q = _w.Query<A, B, C, D>();
        var seen = Entity.Null;
        q.Each((ents, a, b, c, d) =>
            {
                if (ents.Length > 0) seen = ents[0];
            }
        );

        Assert.Equal(e, seen);
    }

    [Fact]
    public void RegisterSystem_ThreeComponents_Progress()
    {
        var e = _w.CreateEntity();
        _w.Set(e, new A { X = 0f });
        _w.Set(e, new B { X = 2f });
        _w.Set(e, new C { X = 3f });

        _w.RegisterSystem<A, B, C>(
            "Abc",
            EcsPhase.OnUpdate,
            (a, b, c) =>
            {
                for (var i = 0; i < a.Length; i++) a[i].X += b[i].X * c[i].X;
            }
        );

        _w.Progress();

        Assert.Equal(6f, _w.Get<A>(e).X);
    }

    [Fact]
    public void RegisterSystem_FourComponents_Progress()
    {
        var e = _w.CreateEntity();
        _w.Set(e, new A { X = 0f });
        _w.Set(e, new B { X = 2f });
        _w.Set(e, new C { X = 3f });
        _w.Set(e, new D { X = 4f });

        _w.RegisterSystem<A, B, C, D>(
            "Abcd",
            EcsPhase.OnUpdate,
            (a, b, c, d) =>
            {
                for (var i = 0; i < a.Length; i++) a[i].X += b[i].X * c[i].X * d[i].X;
            }
        );

        _w.Progress();

        Assert.Equal(24f, _w.Get<A>(e).X);
    }

    // ── Iterator cleanup on callback exceptions ──────────────────────────────

    [Fact]
    public void Each_Callback_Throw_Leaves_Query_Usable()
    {
        for (var i = 0; i < 5; i++)
            _w.Set(_w.CreateEntity(), new A { X = i });

        using var q = _w.Query<A>();
        Assert.Throws<InvalidOperationException>(() =>
            q.Each(span => throw new InvalidOperationException("boom"))
        );

        var count = 0;
        q.Each(span => { count += span.Length; });
        Assert.Equal(5, count);
    }

    [Fact]
    public void ForEach_Callback_Throw_Leaves_Cached_Query_Usable()
    {
        _w.Set(_w.CreateEntity(), new A { X = 3f });
        Assert.Throws<InvalidOperationException>(() =>
            _w.ForEach<A>(span => throw new InvalidOperationException("boom"))
        );

        var sum = 0f;
        _w.ForEach<A>(span =>
            {
                foreach (ref var a in span) sum += a.X;
            }
        );
        Assert.Equal(3f, sum);
    }

    // ── Throwing systems must not break Progress ─────────────────────────────

    [Fact]
    public void Throwing_System_Does_Not_Crash_Progress_Or_Other_Systems()
    {
        _w.Set(_w.CreateEntity(), new A { X = 1f });

        _w.RegisterSystem<A>(
            "Throws",
            EcsPhase.OnUpdate,
            span => throw new InvalidOperationException("boom")
        );
        var ticks = 0;
        _w.RegisterSystem<A>("Counts", EcsPhase.OnUpdate, span => ticks++);

        _w.Progress();
        _w.Progress();

        Assert.Equal(2, ticks);
    }

    private struct A
    {
        public float X;
    }

    private struct B
    {
        public float X;
    }

    private struct C
    {
        public float X;
    }

    private struct D
    {
        public float X;
    }
}
