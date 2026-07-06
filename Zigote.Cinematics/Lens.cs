namespace Zigote.Cinematics;

/// <summary>
///     A photographic lens. <see cref="FocalLengthMm" /> + the sensor drive the field of view;
///     <see cref="FStop" /> + focus distance drive depth of field. The remaining fields feed the
///     native-effect phase (polygonal/anamorphic bokeh, radial distortion) and are ignored until the
///     renderer carries those parameters.
/// </summary>
public readonly struct Lens : IEquatable<Lens>
{
    public float FocalLengthMm { get; init; }
    public float FStop { get; init; }

    /// <summary>0 = circular bokeh; 5..9 = polygonal aperture (native phase).</summary>
    public int ApertureBlades { get; init; }

    /// <summary>1 = spherical; 1.33 / 2.0 = anamorphic horizontal squeeze (native phase).</summary>
    public float Anamorphic { get; init; }

    /// <summary>Radial distortion k1: &lt;0 barrel, &gt;0 pincushion (native phase).</summary>
    public float DistortionK1 { get; init; }

    /// <summary>Radial distortion k2 (higher-order term; native phase).</summary>
    public float DistortionK2 { get; init; }

    public static Lens Default => new() {
        FocalLengthMm = 50f,
        FStop = 2.8f,
        Anamorphic = 1f,
    };

    public bool Equals(Lens other)
    {
        return FocalLengthMm.Equals(other.FocalLengthMm) && FStop.Equals(other.FStop) &&
               ApertureBlades == other.ApertureBlades && Anamorphic.Equals(other.Anamorphic) &&
               DistortionK1.Equals(other.DistortionK1) && DistortionK2.Equals(other.DistortionK2);
    }

    public override bool Equals(object? obj)
    {
        return obj is Lens o && Equals(o);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            FocalLengthMm,
            FStop,
            ApertureBlades,
            Anamorphic,
            DistortionK1,
            DistortionK2
        );
    }
}