namespace Zigote.Core;

/// <summary>
///     A <c>Duration</c> — a span of time built from named units, e.g.
///     <c>new Duration(milliseconds: 300)</c>. Implicitly converts to <c>float</c> seconds, so it
///     drops
///     straight into the framework's seconds-based animation APIs (<c>AnimationController</c>,
///     transition and snackbar constructors) wherever they take a duration.
/// </summary>
public readonly struct Duration : IEquatable<Duration>
{
    public const long MicrosecondsPerMillisecond = 1000;
    public const long MicrosecondsPerSecond = 1000 * MicrosecondsPerMillisecond;
    public const long MicrosecondsPerMinute = 60 * MicrosecondsPerSecond;
    public const long MicrosecondsPerHour = 60 * MicrosecondsPerMinute;
    public const long MicrosecondsPerDay = 24 * MicrosecondsPerHour;

    public static readonly Duration Zero = new();

    public Duration(
        int days = 0,
        int hours = 0,
        int minutes = 0,
        int seconds = 0,
        int milliseconds = 0,
        int microseconds = 0)
    {
        Microseconds =
            (days * MicrosecondsPerDay) +
            (hours * MicrosecondsPerHour) +
            (minutes * MicrosecondsPerMinute) +
            (seconds * MicrosecondsPerSecond) +
            (milliseconds * MicrosecondsPerMillisecond) +
            microseconds;
    }

    private Duration(long microseconds) => Microseconds = microseconds;

    /// <summary>Total length in microseconds.</summary>
    public long Microseconds { get; }

    public long InMicroseconds => Microseconds;
    public long InMilliseconds => Microseconds / MicrosecondsPerMillisecond;
    public double InSeconds => Microseconds / (double)MicrosecondsPerSecond;

    /// <summary>Length in fractional seconds — what the seconds-based animation APIs consume.</summary>
    public float Seconds => (float)InSeconds;

    public static Duration FromSeconds(float seconds) =>
        new((long)MathF.Round(seconds * MicrosecondsPerSecond));

    /// <summary>Drops a <see cref="Duration" /> into any <c>float</c>-seconds slot.</summary>
    public static implicit operator float(Duration d) => d.Seconds;

    public Duration Add(Duration other) => new(Microseconds + other.Microseconds);

    public static Duration operator +(Duration a, Duration b) =>
        new(a.Microseconds + b.Microseconds);

    public static Duration operator -(Duration a, Duration b) =>
        new(a.Microseconds - b.Microseconds);

    public static Duration operator *(Duration a, double factor) =>
        new((long)MathF.Round((float)(a.Microseconds * factor)));

    public bool Equals(Duration other) => Microseconds == other.Microseconds;

    public override bool Equals(object? obj) => obj is Duration d && Equals(d);

    public override int GetHashCode() => Microseconds.GetHashCode();

    public static bool operator ==(Duration a, Duration b) => a.Microseconds == b.Microseconds;

    public static bool operator !=(Duration a, Duration b) => a.Microseconds != b.Microseconds;

    public override string ToString() => $"Duration({InSeconds:0.###}s)";
}
