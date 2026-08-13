namespace Zigote.Cinematics;

/// <summary>
///     Common physical sensor / film gate sizes. <see cref="SensorPreset.Custom" /> uses explicit
///     mm.
/// </summary>
public enum SensorPreset
{
    FullFrame,
    ApsC,
    Micro43,
    Super35,
    Imax,
    Custom,
}

/// <summary>
///     A physical sensor gate in millimetres. Vertical FOV is derived from <see cref="HeightMm" />
///     and the lens focal length; the crop factor is informational (35 mm-equivalent reference).
/// </summary>
public readonly struct SensorFormat : IEquatable<SensorFormat>
{
    public float WidthMm { get; init; }
    public float HeightMm { get; init; }

    /// <summary>35 mm-equivalent crop factor (43.27 mm diagonal / this sensor's diagonal).</summary>
    public float CropFactor => DiagonalMm > 0f ? 43.2666f / DiagonalMm : 1f;

    public float DiagonalMm => MathF.Sqrt(WidthMm * WidthMm + HeightMm * HeightMm);

    public static SensorFormat Of(SensorPreset preset)
    {
        return preset switch {
            SensorPreset.FullFrame => new SensorFormat {
                WidthMm = 36f,
                HeightMm = 24f,
            },
            SensorPreset.ApsC => new SensorFormat {
                WidthMm = 23.6f,
                HeightMm = 15.6f,
            },
            SensorPreset.Micro43 => new SensorFormat {
                WidthMm = 17.3f,
                HeightMm = 13f,
            },
            SensorPreset.Super35 => new SensorFormat {
                WidthMm = 24.89f,
                HeightMm = 18.66f,
            },
            SensorPreset.Imax => new SensorFormat {
                WidthMm = 70.41f,
                HeightMm = 52.63f,
            },
            _ => new SensorFormat {
                WidthMm = 36f,
                HeightMm = 24f,
            },
        };
    }

    public bool Equals(SensorFormat other)
    {
        return WidthMm.Equals(other.WidthMm) && HeightMm.Equals(other.HeightMm);
    }

    public override bool Equals(object? obj)
    {
        return obj is SensorFormat o && Equals(o);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(WidthMm, HeightMm);
    }
}
