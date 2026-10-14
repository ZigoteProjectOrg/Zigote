using System.Diagnostics;
using System.Numerics;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Vfx;

namespace Zigote.RuntimeBench;

/// <summary>
///     Headless probe for one decision: is the AoS <see cref="Particle" /> array worth converting to
///     struct-of-arrays?
///     <para>
///         The case for SoA is that the elementwise passes — gravity, drag, and the
///         position/rotation/age integration — touch a dozen bytes of an 84-byte struct and pay for
///         the whole cache line anyway, and that in parallel float arrays they vectorise. The case
///         against is that it breaks the public <c>ref Particle</c> module API, and that every module
///         which reads several fields at once (turbulence, the over-life curves) gets *worse* when
///         its fields live in different arrays.
///     </para>
///     <para>
///         So the number that decides it is not the speedup on the elementwise passes. It is that
///         speedup multiplied by the share of a real step those passes actually are. This measures
///         both. Run with: <c>dotnet run -c Release --project Zigote.Runtime.Benchmark -- vfx</c>
///     </para>
/// </summary>
internal static class VfxProbe
{
    private const int Steps = 2000;
    private const float Dt = 1f / 120f; // the fixed gameplay/physics step

    // Dead-code sink so the JIT cannot elide a measured pass.
    private static float _sink;

    public static void Run(string[] args)
    {
        int n = args.Length > 0 && int.TryParse(s: args[0], result: out int parsed)
            ? parsed
            : 10_000;

        Console.WriteLine(
            $"Zigote.Vfx particle probe — {n:N0} particles, {Steps:N0} steps @ {Dt * 1000f:F2} ms"
        );
        Console.WriteLine(
            $"Vector<float>.Count = {Vector<float>.Count}"
            + $" ({Vector<float>.Count * 32} bit), hardware accelerated: {Vector.IsHardwareAccelerated}"
        );
        Console.WriteLine(new string(c: '─', count: 78));

        double whole = MeasureWholeStep(n);
        MeasureModules(n);
        double aos = MeasureAos(n);
        double soa = MeasureSoa(n: n, vectorized: false);
        double simd = MeasureSoa(n: n, vectorized: true);

        Console.WriteLine(new string(c: '─', count: 78));
        Report(label: "whole step (4 modules + integrate + compact)", nsPerStep: whole, n: n);
        Report(label: "  elementwise only, AoS (gravity+drag+integrate)", nsPerStep: aos, n: n);
        Report(label: "  elementwise only, SoA scalar", nsPerStep: soa, n: n);
        Report(label: "  elementwise only, SoA + Vector<float>", nsPerStep: simd, n: n);
        Console.WriteLine(new string(c: '─', count: 78));

        double share = aos / whole;
        double speedup = aos / simd;
        // Amdahl: only the elementwise share can improve, and only by `speedup`.
        double wholeGain = 1d / (1d - share + (share / speedup));

        Console.WriteLine($"elementwise share of a real step : {share * 100:F1}%");
        Console.WriteLine($"SoA+SIMD speedup on that share   : {speedup:F2}×");
        Console.WriteLine(
            $"=> best case whole-step speedup  : {wholeGain:F2}×  ({(wholeGain - 1) * 100:F0}% faster)"
        );

        // A ratio is only half the decision. The other half is what the subsystem costs at all:
        // the fixed gameplay step runs at 120 Hz, so a step costing X ns burns 120·X ns of every
        // second. A 30% saving on something that is already a rounding error is not worth an API
        // break, and this is exactly the trap the perf backlog's "profile first" note names.
        double coreShare = whole * 1e-9 * (1d / Dt);
        double saved = coreShare - (coreShare / wholeGain);
        Console.WriteLine();
        Console.WriteLine(
            $"at the 120 Hz fixed step, this emitter costs {coreShare * 100:F2}% of one core;"
        );
        Console.WriteLine($"the whole SoA rewrite would give back {saved * 100:F2}% of one core.");
        Console.WriteLine();
        Console.WriteLine(
            saved >= 0.05d
                ? "VERDICT: worth the API break. Proceed with the SoA rewrite."
                : "VERDICT: NOT worth the API break. The ratio looks good and the absolute number does\n"
                  + "         not — the subsystem is not on anyone's critical path. Spend the effort\n"
                  + "         where the profile says the frame actually goes."
        );
    }

    private static void Report(string label, double nsPerStep, int n) => Console.WriteLine(
        $"{label,-50} {nsPerStep / 1000f,8:F1} µs/step  {nsPerStep / n,6:F2} ns/particle"
    );

    // ── 1. A realistic whole step, through the shipping simulator ─────────────────

    /// <summary>
    ///     The real <see cref="CpuParticleSimulator" /> at a full pool, with the module set a real
    ///     emitter carries: two elementwise force modules and two over-life curves. Emission is off
    ///     and lifetimes are long, so every step measures the same saturated pool — otherwise the
    ///     numbers describe the spawn path instead of the update path.
    /// </summary>
    private static double MeasureWholeStep(int n)
    {
        var asset = new VfxEmitterAsset {
            Capacity = n,
            Looping = true,
            SpawnRate = 0f,
            StartLifetime = new FloatRange(min: 1e6f, max: 1e6f),
            StartSpeed = new FloatRange(min: 1f, max: 4f),
            StartSize = new FloatRange(min: 0.1f, max: 0.4f),
            StartAngularVelocity = new FloatRange(min: -2f, max: 2f),
        };
        asset.Bursts.Add(new VfxBurst(time: 0f, count: n));
        asset.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -9.81f, z: 0f)));
        asset.UpdateModules.Add(new DragModule(0.2f));
        asset.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(from: 1f, to: 0f)));
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(position: 0f, color: Color.White),
                        new ColorStop(position: 1f, color: Color.Black),
                    ]
                )
            )
        );

        var sim = new CpuParticleSimulator(asset);
        sim.Tick(Dt); // fires the burst
        if (sim.Pool.Count < n)
            Console.WriteLine($"  ! pool filled to {sim.Pool.Count:N0} of {n:N0}");

        sim.Emitting = false;
        return Time(steps: Steps, body: () => sim.Tick(Dt));
    }

    // ── 1b. Per-module cost, so "the rest of the step" is not a black box ────────

    /// <summary>
    ///     Each module's <c>ApplyRange</c> over the same saturated pool, plus the two passes the
    ///     simulator owns (integration and the swap-remove compaction). The whole-step number above
    ///     minus the sum of these is emission and bookkeeping.
    /// </summary>
    private static void MeasureModules(int n)
    {
        var items = new Particle[n];
        for (int i = 0; i < n; i++) items[i] = Seed(i);
        var ctx = new VfxUpdateContext(dt: Dt, time: 1f);

        (string Name, VfxUpdateModule Module)[] modules = [
            ("gravity", new GravityModule(new Vec3(x: 0f, y: -9.81f, z: 0f))),
            ("drag", new DragModule(0.2f)),
            ("size-over-life", new SizeOverLifeModule(FloatCurve.Linear(from: 1f, to: 0f))),
            ("color-over-life",
                new ColorOverLifeModule(
                    new ColorRamp(
                        [
                            new ColorStop(position: 0f, color: Color.White),
                            new ColorStop(position: 1f, color: Color.Black),
                        ]
                    )
                )),
            ("turbulence", new TurbulenceModule(strength: 1f, frequency: 2f)),
            ("vortex", new VortexModule(axis: Vec3.Up, strength: 1f)),
        ];

        Console.WriteLine("per-pass, over a saturated pool:");
        foreach ((string name, var module) in modules)
        {
            // `ctx` cannot be captured by a lambda (it is a readonly struct passed by `in`), so copy
            // the two floats it carries and rebuild it inside the closure.
            var m = module;
            double ns = Time(
                steps: Steps,
                body: () => m.ApplyRange(particles: items.AsSpan(), ctx: in ctx)
            );
            Console.WriteLine(
                $"  module {name,-18} {ns / 1000f,8:F1} µs  {ns / n,6:F2} ns/particle"
            );
        }

        double integrate = Time(
            steps: Steps,
            body: () =>
            {
                var span = items.AsSpan();
                for (int i = 0; i < span.Length; i++)
                {
                    ref var p = ref span[i];
                    p.Position += p.Velocity * Dt;
                    p.Rotation += p.AngularVelocity * Dt;
                    p.Age += Dt;
                }
            }
        );
        Console.WriteLine(
            $"  {"integrate",-25} {integrate / 1000f,8:F1} µs  {integrate / n,6:F2} ns/particle"
        );

        // The compaction scan as the simulator writes it: read Age and Lifetime per particle, and
        // swap-remove the expired. Nothing expires here (lifetimes are 1e6), so this measures the
        // scan alone — which is the steady state for a long-lived emitter.
        double compact = Time(
            steps: Steps,
            body: () =>
            {
                int live = n, k = 0;
                while (k < live)
                {
                    if (items[k].Age >= items[k].Lifetime) items[k] = items[--live];
                    else k++;
                }

                _sink += live;
            }
        );
        Console.WriteLine(
            $"  {"compaction scan",-25} {compact / 1000f,8:F1} µs  {compact / n,6:F2} ns/particle"
        );
        Console.WriteLine();
    }

    // ── 2. The elementwise passes, isolated, over the shipping AoS layout ────────

    private static double MeasureAos(int n)
    {
        var items = new Particle[n];
        for (int i = 0; i < n; i++) items[i] = Seed(i);

        var gravity = new Vec3(x: 0f, y: -9.81f, z: 0f);
        const float drag = 0.2f;

        return Time(
            steps: Steps,
            body: () =>
            {
                var span = items.AsSpan();
                var step = gravity * Dt;
                for (int i = 0; i < span.Length; i++) span[i].Velocity += step;

                float keep = MathF.Max(x: 0f, y: 1f - (drag * Dt));
                for (int i = 0; i < span.Length; i++) span[i].Velocity *= keep;

                for (int i = 0; i < span.Length; i++)
                {
                    ref var p = ref span[i];
                    p.Position += p.Velocity * Dt;
                    p.Rotation += p.AngularVelocity * Dt;
                    p.Age += Dt;
                }

                _sink += span[0].Age;
            }
        );
    }

    // ── 3. The same passes over parallel float arrays ────────────────────────────

    private static double MeasureSoa(int n, bool vectorized)
    {
        float[] px = new float[n], py = new float[n], pz = new float[n];
        float[] vx = new float[n], vy = new float[n], vz = new float[n];
        float[] rot = new float[n], av = new float[n], age = new float[n];

        for (int i = 0; i < n; i++)
        {
            var p = Seed(i);
            (px[i], py[i], pz[i]) = (p.Position.X, p.Position.Y, p.Position.Z);
            (vx[i], vy[i], vz[i]) = (p.Velocity.X, p.Velocity.Y, p.Velocity.Z);
            (rot[i], av[i], age[i]) = (p.Rotation, p.AngularVelocity, p.Age);
        }

        const float drag = 0.2f;
        float keep = MathF.Max(x: 0f, y: 1f - (drag * Dt));

        return Time(
            steps: Steps,
            body: () =>
            {
                if (vectorized)
                {
                    // Gravity acts on Y alone; X and Z are pure drag. Kept as three separate passes so
                    // this measures the layout, not a hand-fused kernel the AoS side does not get.
                    AddScaled(dst: vy, value: -9.81f * Dt);
                    Scale(dst: vx, factor: keep);
                    Scale(dst: vy, factor: keep);
                    Scale(dst: vz, factor: keep);
                    FusedAdd(dst: px, src: vx, scale: Dt);
                    FusedAdd(dst: py, src: vy, scale: Dt);
                    FusedAdd(dst: pz, src: vz, scale: Dt);
                    FusedAdd(dst: rot, src: av, scale: Dt);
                    AddScaled(dst: age, value: Dt);
                }
                else
                {
                    for (int i = 0; i < n; i++) vy[i] += -9.81f * Dt;
                    for (int i = 0; i < n; i++) vx[i] *= keep;
                    for (int i = 0; i < n; i++) vy[i] *= keep;
                    for (int i = 0; i < n; i++) vz[i] *= keep;
                    for (int i = 0; i < n; i++) px[i] += vx[i] * Dt;
                    for (int i = 0; i < n; i++) py[i] += vy[i] * Dt;
                    for (int i = 0; i < n; i++) pz[i] += vz[i] * Dt;
                    for (int i = 0; i < n; i++) rot[i] += av[i] * Dt;
                    for (int i = 0; i < n; i++) age[i] += Dt;
                }

                _sink += age[0];
            }
        );
    }

    private static void AddScaled(Span<float> dst, float value)
    {
        int width = Vector<float>.Count, i = 0;
        var add = new Vector<float>(value);
        for (; i <= dst.Length - width; i += width)
        {
            var v = new Vector<float>(dst.Slice(start: i, length: width));
            (v + add).CopyTo(dst.Slice(start: i, length: width));
        }

        for (; i < dst.Length; i++) dst[i] += value;
    }

    private static void Scale(Span<float> dst, float factor)
    {
        int width = Vector<float>.Count, i = 0;
        for (; i <= dst.Length - width; i += width)
        {
            var v = new Vector<float>(dst.Slice(start: i, length: width));
            (v * factor).CopyTo(dst.Slice(start: i, length: width));
        }

        for (; i < dst.Length; i++) dst[i] *= factor;
    }

    private static void FusedAdd(Span<float> dst, ReadOnlySpan<float> src, float scale)
    {
        int width = Vector<float>.Count, i = 0;
        for (; i <= dst.Length - width; i += width)
        {
            var d = new Vector<float>(dst.Slice(start: i, length: width));
            var s = new Vector<float>(src.Slice(start: i, length: width));
            ((s * scale) + d).CopyTo(dst.Slice(start: i, length: width));
        }

        for (; i < dst.Length; i++) dst[i] += src[i] * scale;
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    internal static Particle Seed(int i)
    {
        float f = i * 0.001f;
        return new Particle {
            Position = new Vec3(x: f, y: f * 0.5f, z: -f),
            Velocity = new Vec3(x: MathF.Sin(f), y: 2f, z: MathF.Cos(f)),
            Age = 0f,
            Lifetime = 1e6f,
            Size = 0.2f,
            StartSize = 0.2f,
            Rotation = f,
            AngularVelocity = 1f,
            Color = Color.White,
            StartColor = Color.White,
            Seed = (uint)i,
        };
    }

    /// <summary>Warm past tiered JIT, then take the best of three runs — the least noisy estimator.</summary>
    private static double Time(int steps, Action body)
    {
        for (int i = 0; i < 200; i++) body();

        double best = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < steps; i++) body();
            sw.Stop();
            best = Math.Min(val1: best, val2: sw.Elapsed.TotalMilliseconds * 1e6 / steps);
        }

        return best; // ns per step
    }
}
