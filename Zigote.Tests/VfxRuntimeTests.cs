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
        var a = new CpuParticleSimulator(PointEmitter(40f, 2f));
        var b = new CpuParticleSimulator(PointEmitter(40f, 2f));

        a.Asset.UpdateModules.Add(new GravityModule(new Vec3(0f, -9.8f, 0f)));
        b.Asset.UpdateModules.Add(new GravityModule(new Vec3(0f, -9.8f, 0f)));
        a.Asset.UpdateModules.Add(new TurbulenceModule(2f, 3f));
        b.Asset.UpdateModules.Add(new TurbulenceModule(2f, 3f));

        for (var i = 0; i < 240; i++)
        {
            a.Tick(Dt);
            b.Tick(Dt);
        }

        Assert.Equal(a.Pool.Count, b.Pool.Count);
        Assert.True(a.Pool.Count > 0);
        for (var i = 0; i < a.Pool.Count; i++)
        {
            Assert.Equal(a.Pool.Items[i].Position.X, b.Pool.Items[i].Position.X, 5);
            Assert.Equal(a.Pool.Items[i].Position.Y, b.Pool.Items[i].Position.Y, 5);
            Assert.Equal(a.Pool.Items[i].Position.Z, b.Pool.Items[i].Position.Z, 5);
        }
    }

    [Fact]
    public void ContinuousRate_SpawnsApproximatelyRateTimesTime()
    {
        // Lifetime far exceeds the run so nothing dies — count == total spawned.
        var sim = new CpuParticleSimulator(PointEmitter(10f, 1000f));
        for (var i = 0; i < 60; i++) sim.Tick(Dt); // 1 second

        Assert.InRange(sim.Pool.Count, 9, 11);
    }

    [Fact]
    public void Burst_SpawnsExactCount_Once()
    {
        var asset = PointEmitter(0f, 1000f, 256);
        asset.Bursts.Add(new VfxBurst(0f, 50));
        var sim = new CpuParticleSimulator(asset);

        sim.Tick(Dt);
        Assert.Equal(50, sim.Pool.Count);

        for (var i = 0; i < 30; i++) sim.Tick(Dt);
        Assert.Equal(50, sim.Pool.Count); // burst is one-shot within the (infinite) cycle
    }

    [Fact]
    public void Particles_RecycleAfterLifetime()
    {
        var sim = new CpuParticleSimulator(PointEmitter(60f, 0.5f));

        // Run well past lifetime; count must stabilise around rate*lifetime, never grow unbounded.
        for (var i = 0; i < 300; i++) sim.Tick(Dt);
        var settled = sim.Pool.Count;
        Assert.InRange(settled, 20, 40); // ~60/s * 0.5s == ~30

        for (var i = 0; i < 300; i++) sim.Tick(Dt);
        Assert.InRange(sim.Pool.Count, settled - 5, settled + 5);
    }

    [Fact]
    public void Capacity_IsNeverExceeded()
    {
        var sim = new CpuParticleSimulator(PointEmitter(1000f, 1000f, 16));
        for (var i = 0; i < 120; i++) sim.Tick(Dt);
        Assert.Equal(16, sim.Pool.Count);
    }

    [Fact]
    public void Gravity_AccumulatesDownwardVelocity()
    {
        var asset = PointEmitter(0f, 1000f, 8);
        asset.Bursts.Add(new VfxBurst(0f, 1));
        asset.UpdateModules.Add(new GravityModule(new Vec3(0f, -10f, 0f)));
        var sim = new CpuParticleSimulator(asset);

        for (var i = 0; i < 60; i++) sim.Tick(Dt);

        Assert.Equal(1, sim.Pool.Count);
        var p = sim.Pool.Items[0];
        Assert.True(p.Velocity.Y < 0f, "gravity should pull velocity downward");
        Assert.True(p.Position.Y < 0f, "particle should have fallen below the origin");
    }

    [Fact]
    public void SizeOverLife_ScalesFromStartSize()
    {
        var asset = PointEmitter(0f, 1f, 8);
        asset.StartSize = FloatRange.Constant(2f);
        asset.Bursts.Add(new VfxBurst(0f, 1));
        asset.UpdateModules.Add(
            new SizeOverLifeModule(FloatCurve.Linear(1f, 0f))
        ); // shrink to nothing
        var sim = new CpuParticleSimulator(asset);

        sim.Tick(Dt); // born + one update at ~age 0
        var early = sim.Pool.Items[0].Size;
        for (var i = 0; i < 40; i++) sim.Tick(Dt);
        var late = sim.Pool.Items[0].Size;

        Assert.True(early > late, "size should shrink as the particle ages");
        Assert.True(early <= 2f + 1e-3f);
    }

    [Fact]
    public void NonLooping_FinishesAfterDuration()
    {
        var asset = PointEmitter(60f, 0.25f, 256);
        asset.Looping = false;
        asset.Duration = 0.2f;
        var sim = new CpuParticleSimulator(asset);

        for (var i = 0; i < 120; i++) sim.Tick(Dt); // 2s — long past duration + lifetime
        Assert.Equal(0, sim.Pool.Count);
        Assert.False(sim.IsAlive);
    }

    [Fact]
    public void Step_AllocatesZero_OnSteadyState()
    {
        var asset = PointEmitter(120f, 1f, 512);
        asset.UpdateModules.Add(new GravityModule(new Vec3(0f, -9.8f, 0f)));
        asset.UpdateModules.Add(new DragModule(0.2f));
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(0f, Color.Yellow), new ColorStop(1f, Color.Red),
                    ]
                )
            )
        );
        asset.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(1f, 0f)));
        var sim = new CpuParticleSimulator(asset);

        // Warm past JIT and reach the births==deaths steady state (pool array already grown).
        for (var i = 0; i < 400; i++) sim.Tick(Dt);
        Assert.True(sim.Pool.Count > 0);

        const int steps = 600;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < steps; i++) sim.Tick(Dt);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"Steady-state simulation allocated {allocated} B over {steps} steps; expected 0."
        );
    }

    [Fact]
    public void Reset_RestoresDeterministicStart()
    {
        var sim = new CpuParticleSimulator(PointEmitter(40f, 2f));
        for (var i = 0; i < 100; i++) sim.Tick(Dt);
        var snapshot = sim.Pool.Count;
        Assert.True(snapshot > 0);

        sim.Reset();
        Assert.Equal(0, sim.Pool.Count);
        Assert.Equal(0f, sim.ElapsedTime);

        for (var i = 0; i < 100; i++) sim.Tick(Dt);
        Assert.Equal(snapshot, sim.Pool.Count);
    }
}