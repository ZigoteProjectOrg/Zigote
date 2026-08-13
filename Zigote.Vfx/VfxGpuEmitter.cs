using Zigote.Core.Math3D;

namespace Zigote.Vfx;

/// <summary>
///     Host-side driver for a GPU-simulated emitter. Owns only the emission *timing* (the spawn
///     accumulator + burst bookkeeping — the GPU owns the particle state) and builds the per-frame
///     params
///     for the compute kernel. Mirrors the emission half of <see cref="CpuParticleSimulator" /> so the
///     GPU
///     path spawns at the same rate; the GPU then simulates the particles. Deterministic timing; the
///     GPU
///     RNG is reseeded per frame so spawned particles vary.
/// </summary>
public sealed class VfxGpuEmitter
{
    private readonly float[] _params = new float[VfxGpuParams.FloatCount];
    private bool[] _burstFired;
    private uint _frame;
    private float _loopTime;
    private float _spawnAccumulator;
    private float _time;
    public bool Emitting = true;
    public Quat Orientation = Quat.Identity;
    public Vec3 Position;

    public VfxGpuEmitter(VfxEmitterAsset asset)
    {
        Asset = asset;
        _burstFired = new bool[asset.Bursts.Count];
    }

    public VfxEmitterAsset Asset { get; }

    public uint Capacity => (uint)Math.Max(1, Asset.Capacity);
    public uint Blend => Asset.Blend == VfxBlendMode.Additive ? 0u : 1u;

    public void Reset()
    {
        _time = 0f;
        _loopTime = 0f;
        _spawnAccumulator = 0f;
        _frame = 0;
        if (_burstFired.Length != Asset.Bursts.Count) _burstFired = new bool[Asset.Bursts.Count];
        else Array.Clear(_burstFired);
    }

    /// <summary>
    ///     Advance emission timing by <paramref name="dt" />; returns the spawn budget for this
    ///     frame.
    /// </summary>
    public int Step(float dt)
    {
        _time += dt;
        _frame++;
        if (!Emitting || dt <= 0f) return 0;

        var prev = _loopTime;
        var next = _loopTime + dt;

        _spawnAccumulator += Asset.SpawnRate * dt;
        var count = (int)_spawnAccumulator;
        _spawnAccumulator -= count;

        for (var i = 0; i < Asset.Bursts.Count; i++)
        {
            if (_burstFired[i]) continue;
            var b = Asset.Bursts[i];
            if (b.Time < prev || b.Time >= next) continue;
            count += b.Count;
            _burstFired[i] = true;
        }

        _loopTime = next;
        if (Asset.Duration > 0f && _loopTime >= Asset.Duration)
        {
            if (Asset.Looping)
            {
                _loopTime -= Asset.Duration;
                Array.Clear(_burstFired);
            }
            else
            {
                Emitting = false;
            }
        }

        return count;
    }

    /// <summary>
    ///     Build the compute-kernel params for this frame (valid until the next call — reused
    ///     buffer).
    /// </summary>
    public ReadOnlySpan<float> BuildParams(int spawnCount, float dt)
    {
        var frameSeed = unchecked(_frame * 2654435761u + Asset.Seed);
        VfxGpuParams.Build(
            Asset,
            spawnCount,
            frameSeed,
            dt,
            _time,
            Position,
            Orientation,
            _params
        );
        return _params;
    }
}
