using Zigote.Core.Math3D;

namespace Zigote.Vfx;

/// <summary>
///     A single live emitter, simulated on the CPU. Owns a <see cref="ParticlePool" /> and the
///     per-emitter
///     emission/RNG state, and advances both each <see cref="Tick" />. Deterministic for a given asset
///     +
///     dt sequence, so it doubles as the editor preview and the headless test/GPU-parity oracle. The
///     host
///     positions/orients it (<see cref="Position" />/<see cref="Orientation" />) before ticking.
/// </summary>
public sealed class CpuParticleSimulator
{
    public Quat Orientation = Quat.Identity;
    public Vec3 Position;
    private bool[] _burstFired;
    private bool _finished;
    private float _loopTime;
    private VfxRng _rng;
    private float _spawnAccumulator;

    public CpuParticleSimulator(VfxEmitterAsset asset)
    {
        Asset = asset;
        Pool = new ParticlePool(asset.Capacity);
        _rng = new VfxRng(asset.Seed);
        _burstFired = new bool[asset.Bursts.Count];
    }

    public VfxEmitterAsset Asset { get; }
    public ParticlePool Pool { get; }

    public bool Emitting { get; set; } = true;

    public float ElapsedTime { get; private set; }

    /// <summary>Dead once it has stopped emitting (non-looping, past duration) and all particles expired.</summary>
    public bool IsAlive => !_finished || Pool.Count > 0;

    public void Reset()
    {
        Pool.Clear();
        _rng = new VfxRng(Asset.Seed);
        ElapsedTime = 0f;
        _loopTime = 0f;
        _spawnAccumulator = 0f;
        _finished = false;
        if (_burstFired.Length != Asset.Bursts.Count) _burstFired = new bool[Asset.Bursts.Count];
        else Array.Clear(_burstFired);
    }

    public void Tick(float dt)
    {
        if (dt <= 0f) return;
        ElapsedTime += dt;

        if (Emitting && !_finished) Emit(dt);
        UpdateParticles(dt);
    }

    /// <summary>Manually spawn <paramref name="count" /> particles immediately (scripting bursts).</summary>
    public void Burst(int count)
    {
        for (int i = 0; i < count; i++) TrySpawn();
    }

    private void Emit(float dt)
    {
        float prev = _loopTime;
        float next = _loopTime + dt;

        _spawnAccumulator += Asset.SpawnRate * dt;
        int spawnCount = (int)_spawnAccumulator;
        _spawnAccumulator -= spawnCount;
        for (int i = 0; i < spawnCount; i++) TrySpawn();

        for (int i = 0; i < Asset.Bursts.Count; i++)
        {
            if (_burstFired[i]) continue;
            var b = Asset.Bursts[i];
            if (b.Time < prev || b.Time >= next) continue;
            for (int k = 0; k < b.Count; k++) TrySpawn();
            _burstFired[i] = true;
        }

        _loopTime = next;

        if (Asset.Duration <= 0f || _loopTime < Asset.Duration) return;
        if (Asset.Looping)
        {
            _loopTime -= Asset.Duration;
            Array.Clear(_burstFired);
        }
        else
            _finished = true;
    }

    private bool TrySpawn()
    {
        if (!Pool.TryEmit(out int idx)) return false;
        InitParticle(ref Pool.At(idx));
        return true;
    }

    private void InitParticle(ref Particle p)
    {
        p.Age = 0f;
        p.Lifetime = MathF.Max(x: 0.0001f, y: Asset.StartLifetime.Sample(ref _rng));
        p.Seed = _rng.NextUInt();

        SampleShape(position: out var localPos, direction: out var localDir);

        p.Position = Asset.Space == SimulationSpace.World
            ? Position + Orientation.RotateVec(localPos)
            : localPos;

        float speed = Asset.StartSpeed.Sample(ref _rng);
        p.Velocity = Orientation.RotateVec(localDir) * speed;

        p.StartSize = Asset.StartSize.Sample(ref _rng);
        p.Size = p.StartSize;
        p.Rotation = Asset.StartRotation.Sample(ref _rng);
        p.AngularVelocity = Asset.StartAngularVelocity.Sample(ref _rng);

        float ct = _rng.NextFloat();
        p.StartColor = VfxMath.LerpColor(a: Asset.StartColor, b: Asset.StartColorVariation, t: ct);
        p.Color = p.StartColor;
    }

    private void SampleShape(out Vec3 position, out Vec3 direction)
    {
        var axis = Asset.EmitDirection.LengthSq() > 0f ? Asset.EmitDirection.Normalize() : Vec3.Up;
        Basis(axis: axis, tangent: out var tangent, bitangent: out var bitangent);

        switch (Asset.Shape)
        {
            case EmissionShape.Point:
                position = Vec3.Zero;
                direction = axis;
                break;

            case EmissionShape.Sphere:
            {
                var p = _rng.InsideUnitSphere() * Asset.ShapeRadius;
                position = p;
                direction = p.LengthSq() > 0f ? p.Normalize() : axis;
                break;
            }

            case EmissionShape.Hemisphere:
            {
                var p = _rng.InsideUnitSphere() * Asset.ShapeRadius;
                if (p.Dot(axis) < 0f) p = p - (axis * (2f * p.Dot(axis)));
                position = p;
                direction = p.LengthSq() > 0f ? p.Normalize() : axis;
                break;
            }

            case EmissionShape.Box:
                position = new Vec3(
                    x: _rng.Signed() * Asset.ShapeBoxHalfExtents.X,
                    y: _rng.Signed() * Asset.ShapeBoxHalfExtents.Y,
                    z: _rng.Signed() * Asset.ShapeBoxHalfExtents.Z
                );
                direction = axis;
                break;

            case EmissionShape.Circle:
            {
                float phi = _rng.NextFloat() * MathF.Tau;
                var radial = (tangent * MathF.Cos(phi)) + (bitangent * MathF.Sin(phi));
                position = radial * Asset.ShapeRadius;
                direction = radial;
                break;
            }

            case EmissionShape.Cone:
            default:
            {
                float phiPos = _rng.NextFloat() * MathF.Tau;
                float rPos = MathF.Sqrt(_rng.NextFloat()) * Asset.ShapeRadius;
                position = ((tangent * MathF.Cos(phiPos)) + (bitangent * MathF.Sin(phiPos))) * rPos;

                float cosA = MathF.Cos(Asset.ConeAngleDegrees * (MathF.PI / 180f));
                float z = _rng.Range(min: cosA, max: 1f);
                float s = MathF.Sqrt(MathF.Max(x: 0f, y: 1f - (z * z)));
                float phiDir = _rng.NextFloat() * MathF.Tau;
                direction = (axis * z) +
                            (((tangent * MathF.Cos(phiDir)) + (bitangent * MathF.Sin(phiDir))) * s);
                break;
            }
        }
    }

    private void UpdateParticles(float dt)
    {
        var ctx = new VfxUpdateContext(dt: dt, time: ElapsedTime);
        var items = Pool.Items;
        var modules = Asset.UpdateModules;

        // Module-major: one (devirtualizable) call per module over the whole live span, instead of
        // a virtual call per particle per module. Modules only touch their own particle, so the
        // per-particle result is identical to the old particle-major order.
        var live = items.AsSpan(start: 0, length: Pool.Count);
        for (int m = 0; m < modules.Count; m++)
            modules[m].ApplyRange(particles: live, ctx: in ctx);

        // Integrate first, compact second: fusing KillAt into the integration loop copied an
        // 84-byte struct backwards into the loop's own read stream (a store-forward hazard), and
        // kept the loop from ever being a straight-line span pass. A killed particle's swap-in is
        // already integrated, so the split is behaviour-identical.
        for (int i = 0; i < live.Length; i++)
        {
            ref var p = ref live[i];
            p.Position += p.Velocity * dt;
            p.Rotation += p.AngularVelocity * dt;
            p.Age += dt;
        }

        int n = 0;
        while (n < Pool.Count)
        {
            if (items[n].Age >= items[n].Lifetime) Pool.KillAt(n);
            else n++;
        }
    }

    // Orthonormal tangent basis around `axis`, picking a stable reference to avoid degeneracy.
    private static void Basis(Vec3 axis, out Vec3 tangent, out Vec3 bitangent)
    {
        var reference = MathF.Abs(axis.Y) < 0.99f ? Vec3.Up : Vec3.Right;
        tangent = reference.Cross(axis).Normalize();
        bitangent = axis.Cross(tangent);
    }
}
