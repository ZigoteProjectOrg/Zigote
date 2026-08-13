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
                new ColorStop(position: 0f, color: new Color(r: 0f, g: 0f, b: 0f)),
                new ColorStop(position: 1f, color: new Color(r: 1f, g: 1f, b: 1f)),
            ]
        );

        Assert.Equal(expected: 0f, actual: ramp.Evaluate(0f).R, precision: 4);
        Assert.Equal(expected: 0.5f, actual: ramp.Evaluate(0.5f).R, precision: 4);
        Assert.Equal(expected: 1f, actual: ramp.Evaluate(1f).R, precision: 4);

        // Out-of-range clamps to the endpoints.
        Assert.Equal(expected: 0f, actual: ramp.Evaluate(-1f).R, precision: 4);
        Assert.Equal(expected: 1f, actual: ramp.Evaluate(2f).R, precision: 4);
    }

    [Fact]
    public void ColorRamp_SortsUnorderedStops()
    {
        var ramp = new ColorRamp(
            [
                new ColorStop(position: 1f, color: new Color(r: 1f, g: 0f, b: 0f)),
                new ColorStop(position: 0f, color: new Color(r: 0f, g: 0f, b: 0f)),
            ]
        );
        Assert.Equal(expected: 0.5f, actual: ramp.Evaluate(0.5f).R, precision: 4);
    }

    [Fact]
    public void FloatCurve_PiecewiseLinear()
    {
        var curve = new FloatCurve(
            [
                new CurveKey(position: 0f, value: 0f),
                new CurveKey(position: 0.5f, value: 1f),
                new CurveKey(position: 1f, value: 0f),
            ]
        );

        Assert.Equal(expected: 0f, actual: curve.Evaluate(0f), precision: 4);
        Assert.Equal(expected: 0.5f, actual: curve.Evaluate(0.25f), precision: 4);
        Assert.Equal(expected: 1f, actual: curve.Evaluate(0.5f), precision: 4);
        Assert.Equal(expected: 0.5f, actual: curve.Evaluate(0.75f), precision: 4);
        Assert.Equal(expected: 0f, actual: curve.Evaluate(1f), precision: 4);
    }

    [Fact]
    public void Rng_IsDeterministicAndInRange()
    {
        var a = new VfxRng(123);
        var b = new VfxRng(123);
        for (int i = 0; i < 1000; i++)
        {
            float x = a.NextFloat();
            Assert.Equal(expected: x, actual: b.NextFloat(), precision: 6);
            Assert.InRange(actual: x, low: 0f, high: 1f);
        }
    }

    [Fact]
    public void Rng_OnUnitSphere_IsNormalized()
    {
        var rng = new VfxRng(7);
        for (int i = 0; i < 200; i++)
        {
            var v = rng.OnUnitSphere();
            Assert.Equal(expected: 1f, actual: v.Length(), precision: 3);
        }
    }
}
