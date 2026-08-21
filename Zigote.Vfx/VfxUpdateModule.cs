using Zigote.Core.Math3D;

namespace Zigote.Vfx;

public enum VfxModuleKind
{
    Gravity,
    Drag,
    Turbulence,
    Vortex,
    ColorOverLife,
    SizeOverLife,
    AlphaOverLife,
}

public readonly struct VfxUpdateContext(float dt, float time)
{
    public readonly float Dt = dt;
    public readonly float Time = time;
}

// Update modules run per-particle, per-step, in list order. They mutate velocity (forces) or current
// colour/size (over-life). Base integration (position/rotation/age) is the simulator's job and always
// runs after the modules. `Kind` lets the GPU lowering map each module to a WGSL snippet later.
public abstract class VfxUpdateModule
{
    public abstract VfxModuleKind Kind { get; }
    public abstract void Apply(ref Particle p, in VfxUpdateContext ctx);

    /// <summary>
    ///     Apply to a whole live span — one virtual call per module per step instead of one per
    ///     particle per module (~5M indirect calls/s at 10k particles × 4 modules × 120 Hz). A
    ///     sealed override's inner Apply devirtualizes and inlines; this base fallback keeps
    ///     third-party modules working unchanged.
    /// </summary>
    public virtual void ApplyRange(Span<Particle> particles, in VfxUpdateContext ctx)
    {
        for (int i = 0; i < particles.Length; i++) Apply(p: ref particles[i], ctx: in ctx);
    }
}

public sealed class GravityModule(Vec3 gravity) : VfxUpdateModule
{
    public Vec3 Gravity = gravity;
    public override VfxModuleKind Kind => VfxModuleKind.Gravity;

    public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
        p.Velocity += Gravity * ctx.Dt;

    public override void ApplyRange(Span<Particle> particles, in VfxUpdateContext ctx)
    {
        var step = Gravity * ctx.Dt;
        for (int i = 0; i < particles.Length; i++) particles[i].Velocity += step;
    }
}

public sealed class DragModule(float drag) : VfxUpdateModule
{
    /// <summary>Linear damping per second (velocity retained = max(0, 1 - drag·dt)).</summary>
    public float Drag = drag;

    public override VfxModuleKind Kind => VfxModuleKind.Drag;

    public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
        p.Velocity *= MathF.Max(x: 0f, y: 1f - (Drag * ctx.Dt));

    public override void ApplyRange(Span<Particle> particles, in VfxUpdateContext ctx)
    {
        float keep = MathF.Max(x: 0f, y: 1f - (Drag * ctx.Dt));
        for (int i = 0; i < particles.Length; i++) particles[i].Velocity *= keep;
    }
}

public sealed class TurbulenceModule(float strength, float frequency) : VfxUpdateModule
{
    public float Frequency = frequency;
    public float Strength = strength;
    public override VfxModuleKind Kind => VfxModuleKind.Turbulence;

    // Cheap, smooth, deterministic divergence-y field: decorrelated sines with a per-particle phase from
    // the particle seed. Pure function of (position, time, seed), so it ports verbatim to the GPU kernel.
    public override void Apply(ref Particle p, in VfxUpdateContext ctx)
    {
        float phase = (p.Seed & 0xFFFFu) * (MathF.Tau / 65536f);
        float t = ctx.Time;
        float fx = MathF.Sin((p.Position.Y * Frequency) + t + phase);
        float fy = MathF.Sin((p.Position.Z * Frequency) + (t * 1.3f) + phase);
        float fz = MathF.Sin((p.Position.X * Frequency) + (t * 0.7f) + phase);
        p.Velocity += new Vec3(x: fx, y: fy, z: fz) * (Strength * ctx.Dt);
    }
}

public sealed class VortexModule(Vec3 axis, float strength) : VfxUpdateModule
{
    public Vec3 Axis = axis.Normalize();
    public float Strength = strength;
    public override VfxModuleKind Kind => VfxModuleKind.Vortex;

    public override void Apply(ref Particle p, in VfxUpdateContext ctx)
    {
        // Tangential push around the axis through the (emitter-local) origin.
        p.Velocity += Axis.Cross(p.Position) * (Strength * ctx.Dt);
    }
}

public sealed class ColorOverLifeModule(ColorRamp ramp) : VfxUpdateModule
{
    public ColorRamp Ramp = ramp;
    public override VfxModuleKind Kind => VfxModuleKind.ColorOverLife;

    public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
        p.Color = Ramp.Evaluate(p.NormalizedAge);
}

public sealed class SizeOverLifeModule(FloatCurve curve) : VfxUpdateModule
{
    public FloatCurve Curve = curve;
    public override VfxModuleKind Kind => VfxModuleKind.SizeOverLife;

    public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
        p.Size = p.StartSize * Curve.Evaluate(p.NormalizedAge);
}

public sealed class AlphaOverLifeModule(FloatCurve curve) : VfxUpdateModule
{
    public FloatCurve Curve = curve;
    public override VfxModuleKind Kind => VfxModuleKind.AlphaOverLife;

    public override void Apply(ref Particle p, in VfxUpdateContext ctx) =>
        p.Color = p.Color.WithAlpha(Curve.Evaluate(p.NormalizedAge));
}
