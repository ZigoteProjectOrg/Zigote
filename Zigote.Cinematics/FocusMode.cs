namespace Zigote.Cinematics;

/// <summary>How the focus distance is chosen each frame.</summary>
public enum FocusModeKind
{
    /// <summary>Fixed distance set by the operator (no autofocus).</summary>
    Manual,

    /// <summary>Autofocus on whatever is at the centre of frame.</summary>
    Center,

    /// <summary>Autofocus tracking a specific subject (a scene node / world point).</summary>
    Subject,
}

/// <summary>
///     Focus behaviour. <see cref="SpeedPerSec" /> models autofocus lag as an exponential approach to
///     the target distance (0 = instant snap). Manual focus never lags.
/// </summary>
public readonly struct FocusSettings : IEquatable<FocusSettings>
{
    public FocusModeKind Kind { get; init; }

    /// <summary>Focus distance in metres for <see cref="FocusModeKind.Manual" />.</summary>
    public float ManualDistanceM { get; init; }

    /// <summary>Autofocus approach rate per second (higher = snappier); 0 = instant.</summary>
    public float SpeedPerSec { get; init; }

    public static FocusSettings Default =>
        new() {
            Kind = FocusModeKind.Center,
            ManualDistanceM = 8f,
            SpeedPerSec = 4f,
        };

    public bool Equals(FocusSettings other)
    {
        return Kind == other.Kind && ManualDistanceM.Equals(other.ManualDistanceM) &&
               SpeedPerSec.Equals(other.SpeedPerSec);
    }

    public override bool Equals(object? obj)
    {
        return obj is FocusSettings o && Equals(o);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine((int)Kind, ManualDistanceM, SpeedPerSec);
    }
}
