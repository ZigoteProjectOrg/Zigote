using Zigote.Core;

namespace Zigote.Vfx;

/// <summary>A min..max range sampled uniformly (equal endpoints = a constant).</summary>
public readonly struct FloatRange(float min, float max)
{
    public readonly float Min = min;
    public readonly float Max = max;

    public static FloatRange Constant(float v) => new(min: v, max: v);

    public float Sample(ref VfxRng rng) => rng.Range(min: Min, max: Max);
}

public readonly struct ColorStop(float position, Color color)
{
    public readonly float Position = position;
    public readonly Color Color = color;
}

/// <summary>
///     A color gradient keyed by normalized position. The runtime form is a flat, sorted stop array so
///     it
///     serializes plainly and lowers 1:1 to a WGSL ramp function for the future GPU kernel; the
///     editor's
///     <c>GradientEditor</c> edits the same stops.
/// </summary>
public sealed class ColorRamp
{
    private readonly ColorStop[] _stops;

    public ColorRamp(IEnumerable<ColorStop> stops)
    {
        _stops = stops.OrderBy(s => s.Position).ToArray();
        if (_stops.Length == 0) _stops = [new ColorStop(position: 0f, color: Color.White)];
    }

    public IReadOnlyList<ColorStop> Stops => _stops;

    public static ColorRamp Solid(Color c) => new(
        [new ColorStop(position: 0f, color: c), new ColorStop(position: 1f, color: c)]
    );

    public Color Evaluate(float t)
    {
        if (_stops.Length == 1 || t <= _stops[0].Position) return _stops[0].Color;
        var last = _stops[^1];
        if (t >= last.Position) return last.Color;

        for (int i = 1; i < _stops.Length; i++)
        {
            var b = _stops[i];
            if (t > b.Position) continue;
            var a = _stops[i - 1];
            float span = b.Position - a.Position;
            float k = span <= 0f ? 0f : (t - a.Position) / span;
            return VfxMath.LerpColor(a: a.Color, b: b.Color, t: k);
        }

        return last.Color;
    }
}

public readonly struct CurveKey(float position, float value)
{
    public readonly float Position = position;
    public readonly float Value = value;
}

/// <summary>Piecewise-linear scalar curve over normalized position (e.g. size- or alpha-over-life).</summary>
public sealed class FloatCurve
{
    private readonly CurveKey[] _keys;

    public FloatCurve(IEnumerable<CurveKey> keys)
    {
        _keys = keys.OrderBy(k => k.Position).ToArray();
        if (_keys.Length == 0) _keys = [new CurveKey(position: 0f, value: 1f)];
    }

    public IReadOnlyList<CurveKey> Keys => _keys;

    public static FloatCurve Constant(float v) => new(
        [new CurveKey(position: 0f, value: v), new CurveKey(position: 1f, value: v)]
    );

    public static FloatCurve Linear(float from, float to) => new(
        [new CurveKey(position: 0f, value: from), new CurveKey(position: 1f, value: to)]
    );

    public float Evaluate(float t)
    {
        if (_keys.Length == 1 || t <= _keys[0].Position) return _keys[0].Value;
        var last = _keys[^1];
        if (t >= last.Position) return last.Value;

        for (int i = 1; i < _keys.Length; i++)
        {
            var b = _keys[i];
            if (t > b.Position) continue;
            var a = _keys[i - 1];
            float span = b.Position - a.Position;
            float k = span <= 0f ? 0f : (t - a.Position) / span;
            return a.Value + ((b.Value - a.Value) * k);
        }

        return last.Value;
    }
}

internal static class VfxMath
{
    public static Color LerpColor(Color a, Color b, float t)
    {
        return new Color(
            r: a.R + ((b.R - a.R) * t),
            g: a.G + ((b.G - a.G) * t),
            b: a.B + ((b.B - a.B) * t),
            a: a.A + ((b.A - a.A) * t)
        );
    }
}
