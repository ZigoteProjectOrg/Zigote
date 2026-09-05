using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Vfx;

namespace Zigote.RuntimeBench;

/// <summary>
///     Isolates one cost: <see cref="VfxUpdateModule.ApplyRange" /> overridden in a sealed class
///     (the loop sees the exact type, so the inner <c>Apply</c> devirtualizes and inlines) versus
///     inherited from the base (the loop lives in <c>VfxUpdateModule</c>, so every particle costs an
///     interface-shaped virtual call it cannot resolve).
///     <para>
///         Both variants are declared here rather than measured across two builds, because the
///         machine's run-to-run spread — frequency scaling on battery — is wider than the effect
///         being measured. Interleaving them in one process is the only way to get an honest number.
///         The bodies are copied verbatim from the shipping modules; only the presence of the
///         override differs.
///     </para>
/// </summary>
internal static class DispatchProbe
{
    private const int Steps = 2000;
    private const float Dt = 1f / 120f;

    public static void Run(string[] args)
    {
        int n = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 10_000;

        var items = new Particle[n];
        for (int i = 0; i < n; i++) items[i] = VfxProbe.Seed(i);
        var ctx = new VfxUpdateContext(dt: Dt, time: 1f);

        var curve = FloatCurve.Linear(from: 1f, to: 0f);
        var ramp = new ColorRamp([
            new ColorStop(position: 0f, color: Color.White),
            new ColorStop(position: 1f, color: Color.Black),
        ]);

        Console.WriteLine($"ApplyRange dispatch probe — {n:N0} particles, {Steps:N0} steps, interleaved");
        Console.WriteLine(new string('─', 78));

        Compare(name: "size-over-life",
            baseline: new SizeInherited(curve), overridden: new SizeOverridden(curve),
            items: items, ctx: ctx, n: n);
        Compare(name: "color-over-life",
            baseline: new ColorInherited(ramp), overridden: new ColorOverridden(ramp),
            items: items, ctx: ctx, n: n);
        Compare(name: "vortex",
            baseline: new VortexInherited(Vec3.Up, 1f), overridden: new VortexOverridden(Vec3.Up, 1f),
            items: items, ctx: ctx, n: n);
    }

    /// <summary>
    ///     Alternate the two variants run-by-run and take each one's best. A drifting clock then hits
    ///     both equally instead of landing entirely on whichever was measured second.
    /// </summary>
    private static void Compare(string name, VfxUpdateModule baseline, VfxUpdateModule overridden,
        Particle[] items, VfxUpdateContext ctx, int n)
    {
        var c = ctx;
        double bestBase = double.MaxValue, bestOver = double.MaxValue;

        for (int i = 0; i < 400; i++)
        {
            baseline.ApplyRange(items.AsSpan(), in c);
            overridden.ApplyRange(items.AsSpan(), in c);
        }

        for (int round = 0; round < 7; round++)
        {
            bestBase = Math.Min(val1: bestBase, val2: Once(baseline, items, c));
            bestOver = Math.Min(val1: bestOver, val2: Once(overridden, items, c));
        }

        Console.WriteLine(
            $"  {name,-18} inherited {bestBase / 1000f,7:F1} µs   overridden {bestOver / 1000f,7:F1} µs"
            + $"   {bestBase / bestOver,5:F2}×   {(bestBase - bestOver) / n,5:F2} ns/particle saved"
        );
    }

    private static double Once(VfxUpdateModule m, Particle[] items, VfxUpdateContext ctx)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < Steps; i++) m.ApplyRange(items.AsSpan(), in ctx);
        return System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1e6 / Steps;
    }

    // ── The two shapes. Bodies identical; only ApplyRange differs. ───────────────

    private sealed class SizeInherited(FloatCurve curve) : VfxUpdateModule
    {
        public override VfxModuleKind Kind => VfxModuleKind.SizeOverLife;

        public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
            p.Size = p.StartSize * curve.Evaluate(p.NormalizedAge);
    }

    private sealed class SizeOverridden(FloatCurve curve) : VfxUpdateModule
    {
        public override VfxModuleKind Kind => VfxModuleKind.SizeOverLife;

        public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
            p.Size = p.StartSize * curve.Evaluate(p.NormalizedAge);

        public override void ApplyRange(Span<Particle> particles, in VfxUpdateContext ctx)
        {
            for (int i = 0; i < particles.Length; i++) Apply(p: ref particles[i], ctx: in ctx);
        }
    }

    private sealed class ColorInherited(ColorRamp ramp) : VfxUpdateModule
    {
        public override VfxModuleKind Kind => VfxModuleKind.ColorOverLife;

        public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
            p.Color = ramp.Evaluate(p.NormalizedAge);
    }

    private sealed class ColorOverridden(ColorRamp ramp) : VfxUpdateModule
    {
        public override VfxModuleKind Kind => VfxModuleKind.ColorOverLife;

        public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
            p.Color = ramp.Evaluate(p.NormalizedAge);

        public override void ApplyRange(Span<Particle> particles, in VfxUpdateContext ctx)
        {
            for (int i = 0; i < particles.Length; i++) Apply(p: ref particles[i], ctx: in ctx);
        }
    }

    private sealed class VortexInherited(Vec3 axis, float strength) : VfxUpdateModule
    {
        private readonly Vec3 _axis = axis.Normalize();
        public override VfxModuleKind Kind => VfxModuleKind.Vortex;

        public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
            p.Velocity += _axis.Cross(p.Position) * (strength * ctx.Dt);
    }

    private sealed class VortexOverridden(Vec3 axis, float strength) : VfxUpdateModule
    {
        private readonly Vec3 _axis = axis.Normalize();
        public override VfxModuleKind Kind => VfxModuleKind.Vortex;

        public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
            p.Velocity += _axis.Cross(p.Position) * (strength * ctx.Dt);

        public override void ApplyRange(Span<Particle> particles, in VfxUpdateContext ctx)
        {
            for (int i = 0; i < particles.Length; i++) Apply(p: ref particles[i], ctx: in ctx);
        }
    }
}
