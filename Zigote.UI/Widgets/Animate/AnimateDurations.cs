namespace Zigote.UI.Widgets;

/// <summary>
///     flutter_animate-style duration literals so animation code reads naturally:
///     <c>500.ms</c>, <c>0.3.seconds</c>, <c>2.seconds</c>. These are C# 14 extension members over
///     <see cref="int" />/<see cref="double" /> returning a <see cref="TimeSpan" /> — the unit every
///     <c>Animate</c> effect accepts.
/// </summary>
public static class AnimateDurations
{
    extension(int value)
    {
        /// <summary>Milliseconds — e.g. <c>500.ms</c>.</summary>
        public TimeSpan ms => TimeSpan.FromMilliseconds(value);

        /// <summary>Seconds — e.g. <c>2.seconds</c>.</summary>
        public TimeSpan seconds => TimeSpan.FromSeconds(value);

        /// <summary>Minutes.</summary>
        public TimeSpan minutes => TimeSpan.FromMinutes(value);
    }

    extension(double value)
    {
        /// <summary>Milliseconds — e.g. <c>16.7.ms</c>.</summary>
        public TimeSpan ms => TimeSpan.FromMilliseconds(value);

        /// <summary>Seconds — e.g. <c>0.3.seconds</c>.</summary>
        public TimeSpan seconds => TimeSpan.FromSeconds(value);

        /// <summary>Minutes.</summary>
        public TimeSpan minutes => TimeSpan.FromMinutes(value);
    }
}