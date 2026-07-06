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
    private bool[] _burstFired;
    private bool _finished;
    private float _loopTime;
    private VfxRng _rng;
    private float _spawnAccumulator;
    public Quat Orientation = Quat.Identity;
    public Vec3 Position;

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
        for (var i = 0; i < count; i++) TrySpawn();
    }

    private void Emit(float dt)
    {
        var prev = _loopTime;
        var next = _loopTime + dt;

        _spawnAccumulator += Asset.SpawnRate * dt;
        var spawnCount = (int)_spawnAccumulator;
        _spawnAccumulator -= spawnCount;
        for (var i = 0; i < spawnCount; i++) TrySpawn();

        for (var i = 0; i < Asset.Bursts.Count; i++)
        {
            if (_burstFired[i]) continue;
            var b = Asset.Bursts[i];
            if (b.Time < prev || b.Time >= next) continue;
            for (var k = 0; k < b.Count; k++) TrySpawn();
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
        {
            _finished = true;
        }
    }

    private bool TrySpawn()
    {
        if (!Pool.TryEmit(out var idx)) return false;
        InitParticle(ref Pool.At(idx));
        return true;
    }

    private void InitParticle(ref Particle p)
    {
        p.Age = 0f;
        p.Lifetime = MathF.Max(0.0001f, Asset.StartLifetime.Sample(ref _rng));
        p.Seed = _rng.NextUInt();

        SampleShape(out var localPos, out var localDir);

        p.Position = Asset.Space == SimulationSpace.World
            ? Position + Orientation.RotateVec(localPos)
            : localPos;

        var speed = Asset.StartSpeed.Sample(ref _rng);
        p.Velocity = Orientation.RotateVec(localDir) * speed;

        p.StartSize = Asset.StartSize.Sample(ref _rng);
        p.Size = p.StartSize;
        p.Rotation = Asset.StartRotation.Sample(ref _rng);
        p.AngularVelocity = Asset.StartAngularVelocity.Sample(ref _rng);

        var ct = _rng.NextFloat();
        p.StartColor = VfxMath.LerpColor(Asset.StartColor, Asset.StartColorVariation, ct);
        p.Color = p.StartColor;
    }

    private void SampleShape(out Vec3 position, out Vec3 direction)
    {
        var axis = Asset.EmitDirection.LengthSq() > 0f ? Asset.EmitDirection.Normalize() : Vec3.Up;
        Basis(axis, out var tangent, out var bitangent);

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
                if (p.Dot(axis) < 0f) p = p - axis * (2f * p.Dot(axis));
                position = p;
                direction = p.LengthSq() > 0f ? p.Normalize() : axis;
                break;
            }

            case EmissionShape.Box:
                position = new Vec3(
                    _rng.Signed() * Asset.ShapeBoxHalfExtents.X,
                    _rng.Signed() * Asset.ShapeBoxHalfExtents.Y,
                    _rng.Signed() * Asset.ShapeBoxHalfExtents.Z
                );
                direction = axis;
                break;

            case EmissionShape.Circle:
            {
                var phi = _rng.NextFloat() * MathF.Tau;
                var radial = tangent * MathF.Cos(phi) + bitangent * MathF.Sin(phi);
                position = radial * Asset.ShapeRadius;
                direction = radial;
                break;
            }

            case EmissionShape.Cone:
            default:
            {
                var phiPos = _rng.NextFloat() * MathF.Tau;
                var rPos = MathF.Sqrt(_rng.NextFloat()) * Asset.ShapeRadius;
                position = (tangent * MathF.Cos(phiPos) + bitangent * MathF.Sin(phiPos)) * rPos;

                var cosA = MathF.Cos(Asset.ConeAngleDegrees * (MathF.PI / 180f));
                var z = _rng.Range(cosA, 1f);
                var s = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
                var phiDir = _rng.NextFloat() * MathF.Tau;
                direction = axis * z +
                            (tangent * MathF.Cos(phiDir) + bitangent * MathF.Sin(phiDir)) * s;
                break;
            }
        }
    }

    private void UpdateParticles(float dt)
    {
        var ctx = new VfxUpdateContext(dt, ElapsedTime);
        var items = Pool.Items;
        var modules = Asset.UpdateModules;

        var i = 0;
        while (i < Pool.Count)
        {
            ref var p = ref items[i];

            for (var m = 0; m < modules.Count; m++) modules[m].Apply(ref p, in ctx);

            p.Position += p.Velocity * dt;
            p.Rotation += p.AngularVelocity * dt;
            p.Age += dt;

            if (p.Age >= p.Lifetime) Pool.KillAt(i);
            else i++;
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