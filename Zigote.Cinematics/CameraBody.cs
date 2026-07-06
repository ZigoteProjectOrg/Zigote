namespace Zigote.Cinematics;

/// <summary>
///     The exposure-triangle inputs of a camera body. ISO + shutter + the lens f-stop resolve to an
///     exposure value (EV) which maps onto the renderer's exposure multiplier.
///     <see cref="ShutterSpeed" />
///     is also the motion-blur shutter (native phase).
/// </summary>
public readonly struct CameraBody : IEquatable<CameraBody>
{
    public float Iso { get; init; }

    /// <summary>Exposure time in seconds (e.g. 1/50 s = 0.02).</summary>
    public float ShutterSpeed { get; init; }

    /// <summary>Optional cine shutter angle in degrees (0 = use <see cref="ShutterSpeed" /> directly).</summary>
    public float ShutterAngleDeg { get; init; }

    public static CameraBody Default => new() {
        Iso = 100f,
        ShutterSpeed = 1f / 50f,
    };

    public bool Equals(CameraBody other)
    {
        return Iso.Equals(other.Iso) && ShutterSpeed.Equals(other.ShutterSpeed) &&
               ShutterAngleDeg.Equals(other.ShutterAngleDeg);
    }

    public override bool Equals(object? obj)
    {
        return obj is CameraBody o && Equals(o);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Iso, ShutterSpeed, ShutterAngleDeg);
    }
}