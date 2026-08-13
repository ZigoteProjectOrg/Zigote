using Xunit;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Vfx;

namespace Zigote.Tests;

/// <summary>
///     Tests the GPU-compute lowering: <see cref="VfxGpuParams" /> (the 112-float kernel UBO layout —
///     must
///     match particle_compute_source.wgsl field-for-field) and <see cref="VfxGpuEmitter" /> (host
///     emission
///     timing). Headless — no native.
/// </summary>
public class VfxGpuParamsTests
{
    [Fact]
    public void Build_LaysOutCoreFields()
    {
        var asset = new VfxEmitterAsset {
            Capacity = 1024,
            Shape = EmissionShape.Box,
            Space = SimulationSpace.World,
            ShapeRadius = 0.5f,
            ConeAngleDegrees = 0f,
            StartLifetime = new FloatRange(min: 1f, max: 2f),
            StartSpeed = new FloatRange(min: 3f, max: 4f),
            StartSize = new FloatRange(min: 0.1f, max: 0.2f),
            StartColor = new Color(r: 0.1f, g: 0.2f, b: 0.3f),
            ShapeBoxHalfExtents = new Vec3(x: 1f, y: 2f, z: 3f),
            EmitDirection = new Vec3(x: 0f, y: 1f, z: 0f),
        };

        float[] p = VfxGpuParams.Build(
            asset: asset,
            spawnCount: 7,
            frameSeed: 99u,
            dt: 0.016f,
            time: 1.5f,
            position: new Vec3(x: 5f, y: 6f, z: 7f),
            orientation: Quat.Identity
        );

        Assert.Equal(expected: 112, actual: p.Length);
        Assert.Equal(expected: 1024f, actual: p[0]); // capacity
        Assert.Equal(expected: 7f, actual: p[1]); // spawn_count
        Assert.Equal(expected: 99f, actual: p[2]); // frame_seed
        Assert.Equal(expected: (int)EmissionShape.Box, actual: p[3]); // shape
        Assert.Equal(expected: 0f, actual: p[4]); // module mask (no modules)
        Assert.Equal(expected: 0.016f, actual: p[8], precision: 5); // dt
        Assert.Equal(expected: 1.5f, actual: p[9], precision: 5); // time
        Assert.Equal(expected: 5f, actual: p[12]);
        Assert.Equal(expected: 6f, actual: p[13]);
        Assert.Equal(expected: 7f, actual: p[14]); // epos
        Assert.Equal(expected: 1f, actual: p[19]); // erot identity .w
        Assert.Equal(expected: 0.5f, actual: p[24]); // radius
        Assert.Equal(expected: 1f, actual: p[25], precision: 5); // cos(0°)
        Assert.Equal(expected: 1f, actual: p[28]);
        Assert.Equal(expected: 2f, actual: p[29]);
        Assert.Equal(expected: 3f, actual: p[30]); // box extents
        Assert.Equal(expected: 1f, actual: p[32]);
        Assert.Equal(expected: 2f, actual: p[33]);
        Assert.Equal(expected: 3f, actual: p[34]);
        Assert.Equal(expected: 4f, actual: p[35]); // life min/max, speed min/max
        Assert.Equal(expected: 0.1f, actual: p[44], precision: 5);
        Assert.Equal(expected: 0.2f, actual: p[45], precision: 5); // col0
    }

    [Fact]
    public void Build_SetsModuleMaskAndForces()
    {
        var asset = new VfxEmitterAsset();
        asset.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -9.8f, z: 0f)));
        asset.UpdateModules.Add(new DragModule(0.5f));
        asset.UpdateModules.Add(
            new VortexModule(axis: new Vec3(x: 0f, y: 1f, z: 0f), strength: 3f)
        );

        float[] p = VfxGpuParams.Build(
            asset: asset,
            spawnCount: 0,
            frameSeed: 0,
            dt: 0.016f,
            time: 0f,
            position: Vec3.Zero,
            orientation: Quat.Identity
        );

        Assert.Equal(expected: 1f + 2f + 8f, actual: p[4]); // gravity | drag | vortex
        Assert.Equal(expected: -9.8f, actual: p[53], precision: 4); // gravity.y
        Assert.Equal(expected: 0.5f, actual: p[55], precision: 4); // drag
        Assert.Equal(expected: 3f, actual: p[63], precision: 4); // vortex strength
    }

    [Fact]
    public void Build_BakesColorRampIntoLut()
    {
        var asset = new VfxEmitterAsset();
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(position: 0f, color: new Color(r: 0f, g: 0f, b: 0f)),
                        new ColorStop(position: 1f, color: new Color(r: 1f, g: 1f, b: 1f)),
                    ]
                )
            )
        );

        float[] p = VfxGpuParams.Build(
            asset: asset,
            spawnCount: 0,
            frameSeed: 0,
            dt: 0.016f,
            time: 0f,
            position: Vec3.Zero,
            orientation: Quat.Identity
        );

        Assert.Equal(expected: 16f, actual: p[4]); // ColorOverLife bit
        Assert.Equal(expected: 0f, actual: p[64], precision: 4); // stop 0 (t=0) → black
        Assert.Equal(
            expected: 1f,
            actual: p[64 + (7 * 4)],
            precision: 4
        ); // stop 7 (t=1) → white .r
    }

    [Fact]
    public void Build_BakesSizeCurveLut()
    {
        var asset = new VfxEmitterAsset();
        asset.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(from: 1f, to: 0f)));

        float[] p = VfxGpuParams.Build(
            asset: asset,
            spawnCount: 0,
            frameSeed: 0,
            dt: 0.016f,
            time: 0f,
            position: Vec3.Zero,
            orientation: Quat.Identity
        );

        Assert.Equal(expected: 32f, actual: p[4]); // SizeOverLife bit
        Assert.Equal(expected: 1f, actual: p[96], precision: 4); // sample 0 = 1
        Assert.Equal(expected: 0f, actual: p[96 + 7], precision: 4); // sample 7 = 0
    }

    [Fact]
    public void Emitter_SpawnRate_OverOneSecond()
    {
        var emitter = new VfxGpuEmitter(new VfxEmitterAsset { SpawnRate = 60f });
        int total = 0;
        for (int i = 0; i < 60; i++) total += emitter.Step(1f / 60f);
        Assert.InRange(actual: total, low: 58, high: 62);
    }

    [Fact]
    public void Emitter_Burst_IsOneShot()
    {
        var asset = new VfxEmitterAsset { SpawnRate = 0f };
        asset.Bursts.Add(new VfxBurst(time: 0f, count: 25));
        var emitter = new VfxGpuEmitter(asset);

        Assert.Equal(expected: 25, actual: emitter.Step(1f / 60f));
        for (int i = 0; i < 30; i++) Assert.Equal(expected: 0, actual: emitter.Step(1f / 60f));
    }
}
