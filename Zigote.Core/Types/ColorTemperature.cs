using Zigote.Core.Math3D;

namespace Zigote.Core;

/// <summary>
///     Maps a black-body colour temperature (Kelvin) to a normalised RGB tint, using the standard
///     Tanner Helland approximation. ~6500 K returns near-white; lower is warm (red/orange), higher is
///     cool (blue). Used as a per-light tint multiplier so colour temperature can drive lighting with
///     no native change — the editor multiplies a light's base colour by this and pushes the product.
/// </summary>
public static class ColorTemperature
{
    /// <summary>Neutral white point — a temperature at/around this leaves the base colour unchanged.</summary>
    public const float Neutral = 6500f;

    /// <summary>Normalised RGB (0..1) for <paramref name="kelvin" /> (clamped to a sane 1000–40000 K range).</summary>
    public static Vec3 KelvinToRgb(float kelvin)
    {
        var t = Math.Clamp(kelvin, 1000f, 40000f) / 100f;

        float r;
        if (t <= 66f) r = 255f;
        else r = Math.Clamp(329.698727446f * MathF.Pow(t - 60f, -0.1332047592f), 0f, 255f);

        float g;
        if (t <= 66f) g = Math.Clamp(99.4708025861f * MathF.Log(t) - 161.1195681661f, 0f, 255f);
        else g = Math.Clamp(288.1221695283f * MathF.Pow(t - 60f, -0.0755148492f), 0f, 255f);

        float b;
        if (t >= 66f) b = 255f;
        else if (t <= 19f) b = 0f;
        else b = Math.Clamp(138.5177312231f * MathF.Log(t - 10f) - 305.0447927307f, 0f, 255f);

        return new Vec3(r / 255f, g / 255f, b / 255f);
    }
}
