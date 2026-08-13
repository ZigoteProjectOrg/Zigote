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
            ShapeBoxHalfExtents = new Vec3(x: 1f, y: 2f, z: 3f),
            ConeAngleDegrees = 30f,
            EmitDirection = new Vec3(x: 0f, y: 0f, z: 1f),
            StartLifetime = new FloatRange(min: 0.5f, max: 1.5f),
            StartSpeed = new FloatRange(min: 1f, max: 2f),
            StartSize = new FloatRange(min: 0.1f, max: 0.2f),
            StartRotation = new FloatRange(min: 0f, max: 3.14f),
            StartAngularVelocity = new FloatRange(min: -1f, max: 1f),
            StartColor = new Color(r: 1f, g: 0.5f, b: 0.25f),
            StartColorVariation = new Color(
                r: 0.9f,
                g: 0.4f,
                b: 0.2f,
                a: 0.8f
            ),
            Blend = VfxBlendMode.AlphaBlend,
            TexturePath = "textures/spark.png",
            SoftParticles = false,
        };
        a.Bursts.Add(new VfxBurst(time: 0.0f, count: 16));
        a.Bursts.Add(new VfxBurst(time: 1.25f, count: 8));
        a.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -9.8f, z: 0f)));
        a.UpdateModules.Add(new DragModule(0.35f));
        a.UpdateModules.Add(new TurbulenceModule(strength: 1.5f, frequency: 2f));
        a.UpdateModules.Add(new VortexModule(axis: new Vec3(x: 0f, y: 1f, z: 0f), strength: 4f));
        a.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(position: 0f, color: new Color(r: 1f, g: 1f, b: 0f)),
                        new ColorStop(
                            position: 1f,
                            color: new Color(
                                r: 1f,
                                g: 0f,
                                b: 0f,
                                a: 0f
                            )
                        ),
                    ]
                )
            )
        );
        a.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(from: 1f, to: 0f)));
        a.UpdateModules.Add(new AlphaOverLifeModule(FloatCurve.Linear(from: 1f, to: 0.2f)));
        return a;
    }

    [Fact]
    public void RoundTrip_IsStable()
    {
        string json = VfxAssetJson.Serialize(AllModulesAsset());
        string again = VfxAssetJson.Serialize(VfxAssetJson.Deserialize(json));
        Assert.Equal(expected: json, actual: again);
    }

    [Fact]
    public void RoundTrip_PreservesFieldsAndModules()
    {
        var a = VfxAssetJson.Deserialize(VfxAssetJson.Serialize(AllModulesAsset()));

        Assert.Equal(expected: 512, actual: a.Capacity);
        Assert.False(a.Looping);
        Assert.Equal(expected: 2.5f, actual: a.Duration);
        Assert.Equal(expected: SimulationSpace.Local, actual: a.Space);
        Assert.Equal(expected: 0xBEEFu, actual: a.Seed);
        Assert.Equal(expected: EmissionShape.Box, actual: a.Shape);
        Assert.Equal(expected: new Vec3(x: 1f, y: 2f, z: 3f), actual: a.ShapeBoxHalfExtents);
        Assert.Equal(expected: 2, actual: a.Bursts.Count);
        Assert.Equal(expected: 16, actual: a.Bursts[0].Count);
        Assert.Equal(expected: VfxBlendMode.AlphaBlend, actual: a.Blend);
        Assert.Equal(expected: "textures/spark.png", actual: a.TexturePath);
        Assert.False(a.SoftParticles);

        Assert.Equal(expected: 7, actual: a.UpdateModules.Count);
        Assert.Equal(
            expected: new Vec3(x: 0f, y: -9.8f, z: 0f),
            actual: Assert.IsType<GravityModule>(a.UpdateModules[0]).Gravity
        );
        Assert.Equal(expected: 0.35f, actual: Assert.IsType<DragModule>(a.UpdateModules[1]).Drag);
        var turb = Assert.IsType<TurbulenceModule>(a.UpdateModules[2]);
        Assert.Equal(expected: 1.5f, actual: turb.Strength);
        Assert.Equal(expected: 2f, actual: turb.Frequency);
        Assert.Equal(
            expected: 4f,
            actual: Assert.IsType<VortexModule>(a.UpdateModules[3]).Strength
        );
        Assert.Equal(
            expected: 2,
            actual: Assert.IsType<ColorOverLifeModule>(a.UpdateModules[4]).Ramp.Stops.Count
        );
        Assert.Equal(
            expected: 2,
            actual: Assert.IsType<SizeOverLifeModule>(a.UpdateModules[5]).Curve.Keys.Count
        );
        Assert.Equal(
            expected: 2,
            actual: Assert.IsType<AlphaOverLifeModule>(a.UpdateModules[6]).Curve.Keys.Count
        );
    }

    [Theory]
    [InlineData("Sparks")]
    [InlineData("Fire")]
    [InlineData("Smoke")]
    [InlineData("Magic")]
    [InlineData("Rain")]
    public void CompiledPresets_RoundTrip(string preset)
    {
        var compiled = VfxGraphCompiler.Compile(VfxPresets.Create(preset: preset, name: preset));
        Assert.True(compiled.Success);

        string json = VfxAssetJson.Serialize(compiled.Asset);
        string again = VfxAssetJson.Serialize(VfxAssetJson.Deserialize(json));
        Assert.Equal(expected: json, actual: again);
    }

    [Fact]
    public void RoundTrippedAsset_SimulatesIdentically()
    {
        var original = AllModulesAsset();
        var baked = VfxAssetJson.Deserialize(VfxAssetJson.Serialize(original));

        var simA = new CpuParticleSimulator(original) { Emitting = true };
        var simB = new CpuParticleSimulator(baked) { Emitting = true };
        for (int i = 0; i < 120; i++)
        {
            simA.Tick(1f / 60f);
            simB.Tick(1f / 60f);
        }

        Assert.Equal(expected: simA.Pool.Live.Length, actual: simB.Pool.Live.Length);
        for (int i = 0; i < simA.Pool.Live.Length; i++)
        {
            Assert.Equal(expected: simA.Pool.Live[i].Position, actual: simB.Pool.Live[i].Position);
            Assert.Equal(expected: simA.Pool.Live[i].Color, actual: simB.Pool.Live[i].Color);
        }
    }
}
