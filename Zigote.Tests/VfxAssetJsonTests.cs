using Xunit;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Graphs.Vfx;
using Zigote.Vfx;

namespace Zigote.Tests;

/// <summary>
///     Pins the baked-VFX codec (<see cref="VfxAssetJson" />) an exported game ships instead of the
///     node
///     graph: JSON round-trips must preserve every emitter field and module, and a round-tripped asset
///     must simulate identically (the codec feeds the same deterministic CPU sim the editor previews).
/// </summary>
public class VfxAssetJsonTests
{
    private static VfxEmitterAsset AllModulesAsset()
    {
        var a = new VfxEmitterAsset {
            Capacity = 512,
            Looping = false,
            Duration = 2.5f,
            Space = SimulationSpace.Local,
            Seed = 0xBEEF,
            SpawnRate = 48f,
            Shape = EmissionShape.Box,
            ShapeRadius = 0.4f,
            ShapeBoxHalfExtents = new Vec3(1f, 2f, 3f),
            ConeAngleDegrees = 30f,
            EmitDirection = new Vec3(0f, 0f, 1f),
            StartLifetime = new FloatRange(0.5f, 1.5f),
            StartSpeed = new FloatRange(1f, 2f),
            StartSize = new FloatRange(0.1f, 0.2f),
            StartRotation = new FloatRange(0f, 3.14f),
            StartAngularVelocity = new FloatRange(-1f, 1f),
            StartColor = new Color(1f, 0.5f, 0.25f),
            StartColorVariation = new Color(
                0.9f,
                0.4f,
                0.2f,
                0.8f
            ),
            Blend = VfxBlendMode.AlphaBlend,
            TexturePath = "textures/spark.png",
            SoftParticles = false,
        };
        a.Bursts.Add(new VfxBurst(0.0f, 16));
        a.Bursts.Add(new VfxBurst(1.25f, 8));
        a.UpdateModules.Add(new GravityModule(new Vec3(0f, -9.8f, 0f)));
        a.UpdateModules.Add(new DragModule(0.35f));
        a.UpdateModules.Add(new TurbulenceModule(1.5f, 2f));
        a.UpdateModules.Add(new VortexModule(new Vec3(0f, 1f, 0f), 4f));
        a.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(0f, new Color(1f, 1f, 0f)),
                        new ColorStop(
                            1f,
                            new Color(
                                1f,
                                0f,
                                0f,
                                0f
                            )
                        ),
                    ]
                )
            )
        );
        a.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(1f, 0f)));
        a.UpdateModules.Add(new AlphaOverLifeModule(FloatCurve.Linear(1f, 0.2f)));
        return a;
    }

    [Fact]
    public void RoundTrip_IsStable()
    {
        var json = VfxAssetJson.Serialize(AllModulesAsset());
        var again = VfxAssetJson.Serialize(VfxAssetJson.Deserialize(json));
        Assert.Equal(json, again);
    }

    [Fact]
    public void RoundTrip_PreservesFieldsAndModules()
    {
        var a = VfxAssetJson.Deserialize(VfxAssetJson.Serialize(AllModulesAsset()));

        Assert.Equal(512, a.Capacity);
        Assert.False(a.Looping);
        Assert.Equal(2.5f, a.Duration);
        Assert.Equal(SimulationSpace.Local, a.Space);
        Assert.Equal(0xBEEFu, a.Seed);
        Assert.Equal(EmissionShape.Box, a.Shape);
        Assert.Equal(new Vec3(1f, 2f, 3f), a.ShapeBoxHalfExtents);
        Assert.Equal(2, a.Bursts.Count);
        Assert.Equal(16, a.Bursts[0].Count);
        Assert.Equal(VfxBlendMode.AlphaBlend, a.Blend);
        Assert.Equal("textures/spark.png", a.TexturePath);
        Assert.False(a.SoftParticles);

        Assert.Equal(7, a.UpdateModules.Count);
        Assert.Equal(
            new Vec3(0f, -9.8f, 0f),
            Assert.IsType<GravityModule>(a.UpdateModules[0]).Gravity
        );
        Assert.Equal(0.35f, Assert.IsType<DragModule>(a.UpdateModules[1]).Drag);
        var turb = Assert.IsType<TurbulenceModule>(a.UpdateModules[2]);
        Assert.Equal(1.5f, turb.Strength);
        Assert.Equal(2f, turb.Frequency);
        Assert.Equal(4f, Assert.IsType<VortexModule>(a.UpdateModules[3]).Strength);
        Assert.Equal(2, Assert.IsType<ColorOverLifeModule>(a.UpdateModules[4]).Ramp.Stops.Count);
        Assert.Equal(2, Assert.IsType<SizeOverLifeModule>(a.UpdateModules[5]).Curve.Keys.Count);
        Assert.Equal(2, Assert.IsType<AlphaOverLifeModule>(a.UpdateModules[6]).Curve.Keys.Count);
    }

    [Theory]
    [InlineData("Sparks")]
    [InlineData("Fire")]
    [InlineData("Smoke")]
    [InlineData("Magic")]
    [InlineData("Rain")]
    public void CompiledPresets_RoundTrip(string preset)
    {
        var compiled = VfxGraphCompiler.Compile(VfxPresets.Create(preset, preset));
        Assert.True(compiled.Success);

        var json = VfxAssetJson.Serialize(compiled.Asset);
        var again = VfxAssetJson.Serialize(VfxAssetJson.Deserialize(json));
        Assert.Equal(json, again);
    }

    [Fact]
    public void RoundTrippedAsset_SimulatesIdentically()
    {
        var original = AllModulesAsset();
        var baked = VfxAssetJson.Deserialize(VfxAssetJson.Serialize(original));

        var simA = new CpuParticleSimulator(original) { Emitting = true };
        var simB = new CpuParticleSimulator(baked) { Emitting = true };
        for (var i = 0; i < 120; i++)
        {
            simA.Tick(1f / 60f);
            simB.Tick(1f / 60f);
        }

        Assert.Equal(simA.Pool.Live.Length, simB.Pool.Live.Length);
        for (var i = 0; i < simA.Pool.Live.Length; i++)
        {
            Assert.Equal(simA.Pool.Live[i].Position, simB.Pool.Live[i].Position);
            Assert.Equal(simA.Pool.Live[i].Color, simB.Pool.Live[i].Color);
        }
    }
}
