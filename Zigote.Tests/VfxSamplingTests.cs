using Xunit;
using Zigote.Core;
using Zigote.Vfx;
using CurveKey = Zigote.Vfx.CurveKey;

namespace Zigote.Tests;

public class VfxSamplingTests
{
    [Fact]
    public void ColorRamp_InterpolatesAndClamps()
    {
        var ramp = new ColorRamp(
            [
                new ColorStop(0f, new Color(0f, 0f, 0f)),
                new ColorStop(1f, new Color(1f, 1f, 1f)),
            ]
        );

        Assert.Equal(0f, ramp.Evaluate(0f).R, 4);
        Assert.Equal(0.5f, ramp.Evaluate(0.5f).R, 4);
        Assert.Equal(1f, ramp.Evaluate(1f).R, 4);

        // Out-of-range clamps to the endpoints.
        Assert.Equal(0f, ramp.Evaluate(-1f).R, 4);
        Assert.Equal(1f, ramp.Evaluate(2f).R, 4);
    }

    [Fact]
    public void ColorRamp_SortsUnorderedStops()
    {
        var ramp = new ColorRamp(
            [
                new ColorStop(1f, new Color(1f, 0f, 0f)),
                new ColorStop(0f, new Color(0f, 0f, 0f)),
            ]
        );
        Assert.Equal(0.5f, ramp.Evaluate(0.5f).R, 4);
    }

    [Fact]
    public void FloatCurve_PiecewiseLinear()
    {
        var curve = new FloatCurve(
            [
                new CurveKey(0f, 0f),
                new CurveKey(0.5f, 1f),
                new CurveKey(1f, 0f),
            ]
        );

        Assert.Equal(0f, curve.Evaluate(0f), 4);
        Assert.Equal(0.5f, curve.Evaluate(0.25f), 4);
        Assert.Equal(1f, curve.Evaluate(0.5f), 4);
        Assert.Equal(0.5f, curve.Evaluate(0.75f), 4);
        Assert.Equal(0f, curve.Evaluate(1f), 4);
    }

    [Fact]
    public void Rng_IsDeterministicAndInRange()
    {
        var a = new VfxRng(123);
        var b = new VfxRng(123);
        for (var i = 0; i < 1000; i++)
        {
            var x = a.NextFloat();
            Assert.Equal(x, b.NextFloat(), 6);
            Assert.InRange(x, 0f, 1f);
        }
    }

    [Fact]
    public void Rng_OnUnitSphere_IsNormalized()
    {
        var rng = new VfxRng(7);
        for (var i = 0; i < 200; i++)
        {
            var v = rng.OnUnitSphere();
            Assert.Equal(1f, v.Length(), 3);
        }
    }
}