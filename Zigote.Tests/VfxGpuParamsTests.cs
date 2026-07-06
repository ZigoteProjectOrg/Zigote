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
            StartLifetime = new FloatRange(1f, 2f),
            StartSpeed = new FloatRange(3f, 4f),
            StartSize = new FloatRange(0.1f, 0.2f),
            StartColor = new Color(0.1f, 0.2f, 0.3f),
            ShapeBoxHalfExtents = new Vec3(1f, 2f, 3f),
            EmitDirection = new Vec3(0f, 1f, 0f),
        };

        var p = VfxGpuParams.Build(
            asset,
            7,
            99u,
            0.016f,
            1.5f,
            new Vec3(5f, 6f, 7f),
            Quat.Identity
        );

        Assert.Equal(112, p.Length);
        Assert.Equal(1024f, p[0]); // capacity
        Assert.Equal(7f, p[1]); // spawn_count
        Assert.Equal(99f, p[2]); // frame_seed
        Assert.Equal((int)EmissionShape.Box, p[3]); // shape
        Assert.Equal(0f, p[4]); // module mask (no modules)
        Assert.Equal(0.016f, p[8], 5); // dt
        Assert.Equal(1.5f, p[9], 5); // time
        Assert.Equal(5f, p[12]);
        Assert.Equal(6f, p[13]);
        Assert.Equal(7f, p[14]); // epos
        Assert.Equal(1f, p[19]); // erot identity .w
        Assert.Equal(0.5f, p[24]); // radius
        Assert.Equal(1f, p[25], 5); // cos(0°)
        Assert.Equal(1f, p[28]);
        Assert.Equal(2f, p[29]);
        Assert.Equal(3f, p[30]); // box extents
        Assert.Equal(1f, p[32]);
        Assert.Equal(2f, p[33]);
        Assert.Equal(3f, p[34]);
        Assert.Equal(4f, p[35]); // life min/max, speed min/max
        Assert.Equal(0.1f, p[44], 5);
        Assert.Equal(0.2f, p[45], 5); // col0
    }

    [Fact]
    public void Build_SetsModuleMaskAndForces()
    {
        var asset = new VfxEmitterAsset();
        asset.UpdateModules.Add(new GravityModule(new Vec3(0f, -9.8f, 0f)));
        asset.UpdateModules.Add(new DragModule(0.5f));
        asset.UpdateModules.Add(new VortexModule(new Vec3(0f, 1f, 0f), 3f));

        var p = VfxGpuParams.Build(
            asset,
            0,
            0,
            0.016f,
            0f,
            Vec3.Zero,
            Quat.Identity
        );

        Assert.Equal(1f + 2f + 8f, p[4]); // gravity | drag | vortex
        Assert.Equal(-9.8f, p[53], 4); // gravity.y
        Assert.Equal(0.5f, p[55], 4); // drag
        Assert.Equal(3f, p[63], 4); // vortex strength
    }

    [Fact]
    public void Build_BakesColorRampIntoLut()
    {
        var asset = new VfxEmitterAsset();
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(0f, new Color(0f, 0f, 0f)),
                        new ColorStop(1f, new Color(1f, 1f, 1f)),
                    ]
                )
            )
        );

        var p = VfxGpuParams.Build(
            asset,
            0,
            0,
            0.016f,
            0f,
            Vec3.Zero,
            Quat.Identity
        );

        Assert.Equal(16f, p[4]); // ColorOverLife bit
        Assert.Equal(0f, p[64], 4); // stop 0 (t=0) → black
        Assert.Equal(1f, p[64 + 7 * 4], 4); // stop 7 (t=1) → white .r
    }

    [Fact]
    public void Build_BakesSizeCurveLut()
    {
        var asset = new VfxEmitterAsset();
        asset.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(1f, 0f)));

        var p = VfxGpuParams.Build(
            asset,
            0,
            0,
            0.016f,
            0f,
            Vec3.Zero,
            Quat.Identity
        );

        Assert.Equal(32f, p[4]); // SizeOverLife bit
        Assert.Equal(1f, p[96], 4); // sample 0 = 1
        Assert.Equal(0f, p[96 + 7], 4); // sample 7 = 0
    }

    [Fact]
    public void Emitter_SpawnRate_OverOneSecond()
    {
        var emitter = new VfxGpuEmitter(new VfxEmitterAsset { SpawnRate = 60f });
        var total = 0;
        for (var i = 0; i < 60; i++) total += emitter.Step(1f / 60f);
        Assert.InRange(total, 58, 62);
    }

    [Fact]
    public void Emitter_Burst_IsOneShot()
    {
        var asset = new VfxEmitterAsset { SpawnRate = 0f };
        asset.Bursts.Add(new VfxBurst(0f, 25));
        var emitter = new VfxGpuEmitter(asset);

        Assert.Equal(25, emitter.Step(1f / 60f));
        for (var i = 0; i < 30; i++) Assert.Equal(0, emitter.Step(1f / 60f));
    }
}