using System.Diagnostics;
using Zigote.Ecs;

namespace Zigote.EcsBench;

internal struct Position
{
    public float X, Y, Z;
}

internal struct Velocity
{
    public float X, Y, Z;
}

/// <summary>
///     Headless flecs performance probe for the "entity-first inside, OOP outside" decision.
///     Measures the four numbers that drive Option A (flecs as source of truth) vs B:
///       1. spawn throughput,
///       2. system / chunk iteration (the simulation hot path),
///       3. per-entity Get/Set FFI cost (the trap to avoid in bulk code),
///       4. the OnSet observer → ring → drain notification path (flecs → editor).
///     Run with: dotnet run -c Release --project Zigote.Ecs.Benchmark
/// </summary>
internal static class Program
{
    private const int N = 100_000; // entities for spawn + iteration
    private const int Frames = 1000; // sim frames
    private const int ObsEntities = 50_000;
    private const int ObsPerFrame = 5_000; // Sets (→ OnSet events) per frame
    private const int ObsFrames = 200; // → 1,000,000 notification events total

    // Dead-code sinks so the JIT can't elide the measured work.
    private static float _sink;
    private static long _ringCount;
    private static (ulong ent, float x)[] _ring = new (ulong, float)[ObsPerFrame * 2];

    private static int Main(string[] args)
    {
        if (args.Contains("verify")) return Verify.Run();

        Console.WriteLine("=== Zigote ECS (flecs) benchmark ===");
        Console.WriteLine(
            $"runtime {Environment.Version}  GC={(GCSettings_IsServer() ? "server" : "workstation")}  " +
            $"cores={Environment.ProcessorCount}  config={Config()}");
        Console.WriteLine($"  sizeof(Position)={Unsafe_SizeOf<Position>()}B  sizeof(Velocity)={Unsafe_SizeOf<Velocity>()}B");
        Console.WriteLine();

        try
        {
            var (w, ents) = SpawnBenchmark();
            IterationBenchmark(w);
            PerEntityFfiBenchmark(w, ents);
            w.Dispose();

            ObserverBenchmark();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("\nBenchmark failed (native libzigote not loaded?):\n" + ex);
            Environment.Exit(1);
        }

        Console.WriteLine("\nDone.");
        return 0;
    }

    // ── 1. Spawn ─────────────────────────────────────────────────────────────────
    private static (EcsWorld, Entity[]) SpawnBenchmark()
    {
        Console.WriteLine("[1] Spawn");
        var w = new EcsWorld();
        var ents = new Entity[N];
        Collect();
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var ms = TimeMs(() =>
        {
            for (var i = 0; i < N; i++)
            {
                var e = w.CreateEntity();
                w.Set(e, new Position());
                w.Set(e, new Velocity { X = 1, Y = 1, Z = 1 });
                ents[i] = e;
            }
        });
        var allocKb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1024.0;
        Report($"create + 2x Set  ({N:n0} entities)", N, ms);
        Console.WriteLine($"      managed alloc: {allocKb:F0} KB total ({allocKb * 1024 / N:F1} B/entity)\n");
        return (w, ents);
    }

    // ── 2. System / chunk iteration (sim hot path) ───────────────────────────────
    private static void IterationBenchmark(EcsWorld w)
    {
        Console.WriteLine("[2] Simulation iteration  (Position += Velocity)");
        long updates = (long)N * Frames;

        // (a) pull model: managed Query.Each over archetype chunks.
        using (var q = w.Query<Position, Velocity>())
        {
            // warmup
            q.Each(MoveChunk);
            Collect();
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var ms = TimeMs(() =>
            {
                for (var f = 0; f < Frames; f++) q.Each(MoveChunk);
            });
            var alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            Report($"Query.Each chunk iter  ({N:n0} x {Frames} frames)", updates, ms);
            Console.WriteLine($"      managed alloc over {Frames} frames: {alloc} B\n");
        }

        // (b) push model: registered system driven by the flecs pipeline.
        long before = SumX(w);
        w.RegisterSystem<Position, Velocity>("Move", EcsPhase.OnUpdate, MoveChunk);
        w.Progress(); // warmup
        Collect();
        var ms2 = TimeMs(() =>
        {
            for (var f = 0; f < Frames; f++) w.Progress();
        });
        long after = SumX(w);
        long expectedDelta = (long)N * (Frames + 1); // +1 warmup, each frame adds vel.X=1 per entity
        var ran = after - before == expectedDelta;
        if (ran)
            Report($"Pipeline Progress()    ({N:n0} x {Frames} frames)", updates, ms2);
        else
            Console.WriteLine($"   Pipeline Progress()    NOT FUNCTIONAL — system never iterated " +
                              $"(ΣX delta {after - before:n0}, expected {expectedDelta:n0}).\n" +
                              "      flecs phase wiring adds the phase as a bare tag, not a (DependsOn, phase)\n" +
                              "      pair, so the default pipeline excludes it. Use the pull model (Query.Each)\n" +
                              "      — the host owns the frame loop and calls systems-as-queries explicitly.");
        Console.WriteLine();
    }

    private static long SumX(EcsWorld w)
    {
        double sum = 0;
        w.ForEach<Position>(span =>
        {
            foreach (ref var p in span) sum += p.X;
        });
        return (long)sum;
    }

    private static void MoveChunk(Span<Position> pos, Span<Velocity> vel)
    {
        for (var i = 0; i < pos.Length; i++)
        {
            pos[i].X += vel[i].X;
            pos[i].Y += vel[i].Y;
            pos[i].Z += vel[i].Z;
        }
    }

    // ── 3. Per-entity FFI cost (the trap) ────────────────────────────────────────
    private static void PerEntityFfiBenchmark(EcsWorld w, Entity[] ents)
    {
        Console.WriteLine("[3] Per-entity FFI  (one P/Invoke per op — the bulk-loop trap)");
        Collect();
        var msSet = TimeMs(() =>
        {
            for (var i = 0; i < N; i++) w.Set(ents[i], new Position { X = i });
        });
        Report($"Set<Position>  ({N:n0} ops)", N, msSet);

        Collect();
        float acc = 0;
        var msGet = TimeMs(() =>
        {
            for (var i = 0; i < N; i++)
            {
                w.TryGet<Position>(ents[i], out var p);
                acc += p.X;
            }
        });
        _sink += acc;
        Report($"TryGet<Position>  ({N:n0} ops)", N, msGet);
        Console.WriteLine();
    }

    // ── 4. Observer notification path (flecs → editor) ───────────────────────────
    private static void ObserverBenchmark()
    {
        Console.WriteLine("[4] Notification  (OnSet observer → ring → drain)");
        long totalSets = (long)ObsPerFrame * ObsFrames;

        // Baseline: identical Set workload with NO observer registered.
        var (wb, eb) = MakeObsWorld();
        Collect();
        var msBase = TimeMs(() =>
        {
            for (var f = 0; f < ObsFrames; f++)
                for (var k = 0; k < ObsPerFrame; k++)
                    wb.Set(eb[k], new Position { X = f });
        });
        wb.Dispose();
        Report($"Set baseline, no observer  ({totalSets:n0} sets)", totalSets, msBase);

        // With an OnSet observer enqueuing every change into a ring, drained per frame.
        var (wo, eo) = MakeObsWorld();
        _ringCount = 0;
        wo.RegisterObserver<Position>("watch", EcsEvent.OnSet, (entities, data) =>
        {
            for (var i = 0; i < entities.Length; i++)
                if (_ringCount < _ring.Length)
                    _ring[_ringCount++] = (entities[i].Raw, data[i].X);
        });

        Collect();
        var allocA = GC.GetAllocatedBytesForCurrentThread();
        long drainedTotal = 0;
        long drainTicks = 0;
        float dacc = 0;
        var msObs = TimeMs(() =>
        {
            for (var f = 0; f < ObsFrames; f++)
            {
                _ringCount = 0;
                for (var k = 0; k < ObsPerFrame; k++)
                    wo.Set(eo[k], new Position { X = f });

                // Simulated UI-thread drain of this frame's change events.
                // GetTimestamp (not a Stopwatch object) so the harness adds no per-frame allocation —
                // the alloc figure below then reflects only the ECS observe/enqueue path.
                var t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < _ringCount; i++) dacc += _ring[i].x;
                drainTicks += Stopwatch.GetTimestamp() - t0;
                drainedTotal += _ringCount;
            }
        });
        var allocKb = (GC.GetAllocatedBytesForCurrentThread() - allocA) / 1024.0;
        _sink += dacc;
        wo.Dispose();

        var drainMs = drainTicks * 1000.0 / Stopwatch.Frequency;
        Report($"Set + OnSet observe + enqueue  ({totalSets:n0})", totalSets, msObs);
        var observeOnlyMs = msObs - msBase - drainMs;
        Console.WriteLine($"      events captured:    {drainedTotal:n0} / {totalSets:n0} " +
                          $"({(drainedTotal == totalSets ? "lossless" : "RING OVERFLOW")})");
        Console.WriteLine($"      observer overhead:  {observeOnlyMs:F1} ms total  " +
                          $"= {observeOnlyMs * 1e6 / totalSets:F1} ns / event (lock+dict+dispatch+enqueue)");
        Console.WriteLine($"      drain cost:         {drainMs:F1} ms total  " +
                          $"= {drainMs * 1e6 / drainedTotal:F1} ns / event");
        Console.WriteLine($"      managed alloc over {ObsFrames} frames: {allocKb:F1} KB " +
                          $"({(allocKb < 1 ? "≈zero-alloc steady state" : "ALLOCATING")})");
        Console.WriteLine();
    }

    private static (EcsWorld, Entity[]) MakeObsWorld()
    {
        var w = new EcsWorld();
        var ents = new Entity[ObsEntities];
        for (var i = 0; i < ObsEntities; i++)
        {
            ents[i] = w.CreateEntity();
            w.Set(ents[i], new Position());
        }

        return (w, ents);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────
    private static double TimeMs(Action a)
    {
        var sw = Stopwatch.StartNew();
        a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static void Report(string label, long ops, double ms)
    {
        var perSec = ops / (ms / 1000.0);
        Console.WriteLine($"   {label,-44} {ms,9:F2} ms   {perSec / 1e6,8:F1} M/s   {ms * 1e6 / ops,7:F1} ns/op");
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool GCSettings_IsServer()
    {
        return System.Runtime.GCSettings.IsServerGC;
    }

    private static int Unsafe_SizeOf<T>() where T : unmanaged
    {
        return System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
    }

    private static string Config()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}
