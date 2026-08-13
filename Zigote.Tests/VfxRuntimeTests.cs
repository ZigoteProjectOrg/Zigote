using Xunit;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Vfx;

namespace Zigote.Tests;

/// <summary>
///     Logic tests for the headless particle runtime (<see cref="CpuParticleSimulator" /> + the module
///     stack). The CPU simulator is the editor preview AND the determinism oracle the future GPU
///     kernel
///     must match, so determinism and the per-module behaviour are pinned here. No native, no UI.
/// </summary>
public class VfxRuntimeTests
{
    private const float Dt = 1f / 60f;

    private static VfxEmitterAsset PointEmitter(float rate, float lifetime, int capacity = 4096)
    {
        return new VfxEmitterAsset {
            Capacity = capacity,
            Looping = true,
            Shape = EmissionShape.Point,
            SpawnRate = rate,
            StartSpeed = FloatRange.Constant(0f),
            StartLifetime = FloatRange.Constant(lifetime),
            StartSize = FloatRange.Constant(0.1f),
        };
    }

    [Fact]
    public void SameSeed_ProducesIdenticalFrames()
    {
        var a = new CpuParticleSimulator(PointEmitter(rate: 40f, lifetime: 2f));
        var b = new CpuParticleSimulator(PointEmitter(rate: 40f, lifetime: 2f));

        a.Asset.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -9.8f, z: 0f)));
        b.Asset.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -9.8f, z: 0f)));
        a.Asset.UpdateModules.Add(new TurbulenceModule(strength: 2f, frequency: 3f));
        b.Asset.UpdateModules.Add(new TurbulenceModule(strength: 2f, frequency: 3f));

        for (int i = 0; i < 240; i++)
        {
            a.Tick(Dt);
            b.Tick(Dt);
        }

        Assert.Equal(expected: a.Pool.Count, actual: b.Pool.Count);
        Assert.True(a.Pool.Count > 0);
        for (int i = 0; i < a.Pool.Count; i++)
        {
            Assert.Equal(
                expected: a.Pool.Items[i].Position.X,
                actual: b.Pool.Items[i].Position.X,
                precision: 5
            );
            Assert.Equal(
                expected: a.Pool.Items[i].Position.Y,
                actual: b.Pool.Items[i].Position.Y,
                precision: 5
            );
            Assert.Equal(
                expected: a.Pool.Items[i].Position.Z,
                actual: b.Pool.Items[i].Position.Z,
                precision: 5
            );
        }
    }

    [Fact]
    public void ContinuousRate_SpawnsApproximatelyRateTimesTime()
    {
        // Lifetime far exceeds the run so nothing dies — count == total spawned.
        var sim = new CpuParticleSimulator(PointEmitter(rate: 10f, lifetime: 1000f));
        for (int i = 0; i < 60; i++) sim.Tick(Dt); // 1 second

        Assert.InRange(actual: sim.Pool.Count, low: 9, high: 11);
    }

    [Fact]
    public void Burst_SpawnsExactCount_Once()
    {
        var asset = PointEmitter(rate: 0f, lifetime: 1000f, capacity: 256);
        asset.Bursts.Add(new VfxBurst(time: 0f, count: 50));
        var sim = new CpuParticleSimulator(asset);

        sim.Tick(Dt);
        Assert.Equal(expected: 50, actual: sim.Pool.Count);

        for (int i = 0; i < 30; i++) sim.Tick(Dt);
        Assert.Equal(
            expected: 50,
            actual: sim.Pool.Count
        ); // burst is one-shot within the (infinite) cycle
    }

    [Fact]
    public void Particles_RecycleAfterLifetime()
    {
        var sim = new CpuParticleSimulator(PointEmitter(rate: 60f, lifetime: 0.5f));

        // Run well past lifetime; count must stabilise around rate*lifetime, never grow unbounded.
        for (int i = 0; i < 300; i++) sim.Tick(Dt);
        int settled = sim.Pool.Count;
        Assert.InRange(actual: settled, low: 20, high: 40); // ~60/s * 0.5s == ~30

        for (int i = 0; i < 300; i++) sim.Tick(Dt);
        Assert.InRange(actual: sim.Pool.Count, low: settled - 5, high: settled + 5);
    }

    [Fact]
    public void Capacity_IsNeverExceeded()
    {
        var sim = new CpuParticleSimulator(
            PointEmitter(rate: 1000f, lifetime: 1000f, capacity: 16)
        );
        for (int i = 0; i < 120; i++) sim.Tick(Dt);
        Assert.Equal(expected: 16, actual: sim.Pool.Count);
    }

    [Fact]
    public void Gravity_AccumulatesDownwardVelocity()
    {
        var asset = PointEmitter(rate: 0f, lifetime: 1000f, capacity: 8);
        asset.Bursts.Add(new VfxBurst(time: 0f, count: 1));
        asset.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -10f, z: 0f)));
        var sim = new CpuParticleSimulator(asset);

        for (int i = 0; i < 60; i++) sim.Tick(Dt);

        Assert.Equal(expected: 1, actual: sim.Pool.Count);
        var p = sim.Pool.Items[0];
        Assert.True(
            condition: p.Velocity.Y < 0f,
            userMessage: "gravity should pull velocity downward"
        );
        Assert.True(
            condition: p.Position.Y < 0f,
            userMessage: "particle should have fallen below the origin"
        );
    }

    [Fact]
    public void SizeOverLife_ScalesFromStartSize()
    {
        var asset = PointEmitter(rate: 0f, lifetime: 1f, capacity: 8);
        asset.StartSize = FloatRange.Constant(2f);
        asset.Bursts.Add(new VfxBurst(time: 0f, count: 1));
        asset.UpdateModules.Add(
            new SizeOverLifeModule(FloatCurve.Linear(from: 1f, to: 0f))
        ); // shrink to nothing
        var sim = new CpuParticleSimulator(asset);

        sim.Tick(Dt); // born + one update at ~age 0
        float early = sim.Pool.Items[0].Size;
        for (int i = 0; i < 40; i++) sim.Tick(Dt);
        float late = sim.Pool.Items[0].Size;

        Assert.True(
            condition: early > late,
            userMessage: "size should shrink as the particle ages"
        );
        Assert.True(early <= 2f + 1e-3f);
    }

    [Fact]
    public void NonLooping_FinishesAfterDuration()
    {
        var asset = PointEmitter(rate: 60f, lifetime: 0.25f, capacity: 256);
        asset.Looping = false;
        asset.Duration = 0.2f;
        var sim = new CpuParticleSimulator(asset);

        for (int i = 0; i < 120; i++) sim.Tick(Dt); // 2s — long past duration + lifetime
        Assert.Equal(expected: 0, actual: sim.Pool.Count);
        Assert.False(sim.IsAlive);
    }

    [Fact]
    public void Step_AllocatesZero_OnSteadyState()
    {
        var asset = PointEmitter(rate: 120f, lifetime: 1f, capacity: 512);
        asset.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -9.8f, z: 0f)));
        asset.UpdateModules.Add(new DragModule(0.2f));
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(position: 0f, color: Color.Yellow),
                        new ColorStop(position: 1f, color: Color.Red),
                    ]
                )
            )
        );
        asset.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(from: 1f, to: 0f)));
        var sim = new CpuParticleSimulator(asset);

        // Warm past JIT and reach the births==deaths steady state (pool array already grown).
        for (int i = 0; i < 400; i++) sim.Tick(Dt);
        Assert.True(sim.Pool.Count > 0);

        const int steps = 600;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < steps; i++) sim.Tick(Dt);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: allocated == 0,
            userMessage:
            $"Steady-state simulation allocated {allocated} B over {steps} steps; expected 0."
        );
    }

    [Fact]
    public void Reset_RestoresDeterministicStart()
    {
        var sim = new CpuParticleSimulator(PointEmitter(rate: 40f, lifetime: 2f));
        for (int i = 0; i < 100; i++) sim.Tick(Dt);
        int snapshot = sim.Pool.Count;
        Assert.True(snapshot > 0);

        sim.Reset();
        Assert.Equal(expected: 0, actual: sim.Pool.Count);
        Assert.Equal(expected: 0f, actual: sim.ElapsedTime);

        for (int i = 0; i < 100; i++) sim.Tick(Dt);
        Assert.Equal(expected: snapshot, actual: sim.Pool.Count);
    }
}
