using System.Globalization;

namespace Zigote.UI.Charts.Scales;

/// <summary>
///     "Nice numbers" helpers for tick generation (Heckbert's algorithm): tick steps are always
///     1, 2, or 5 × 10^k, and domains round outward to a multiple of the step.
/// </summary>
public static class NiceScale
{
    /// <summary>
    ///     The nice step for spanning <paramref name="range" /> with about
    ///     <paramref name="targetTicks" /> ticks.
    /// </summary>
    public static double TickStep(double range, int targetTicks)
    {
        if (range <= 0 || double.IsNaN(range) || double.IsInfinity(range)) return 1;
        targetTicks = Math.Max(1, targetTicks);
        var rough = range / targetTicks;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var norm = rough / mag; // [1,10)
        var nice = norm switch {
            < 1.5 => 1.0,
            < 3.0 => 2.0,
            < 7.0 => 5.0,
            _ => 10.0,
        };
        return nice * mag;
    }

    /// <summary>
    ///     Round <paramref name="min" />/<paramref name="max" /> outward to multiples of the nice
    ///     step.
    /// </summary>
    public static (double Min, double Max, double Step) NiceDomain(double min, double max,
        int targetTicks)
    {
        if (min > max) (min, max) = (max, min);
        if (min == max)
        {
            // Degenerate single-value domain: open a symmetric unit window around it.
            var pad = min == 0 ? 1 : Math.Abs(min) * 0.5;
            min -= pad;
            max += pad;
        }

        var step = TickStep(max - min, targetTicks);
        var niceMin = Math.Floor(min / step) * step;
        var niceMax = Math.Ceiling(max / step) * step;
        return (niceMin, niceMax, step);
    }

    /// <summary>
    ///     Tick values from <paramref name="min" /> to <paramref name="max" /> inclusive at
    ///     <paramref name="step" />
    ///     intervals.
    /// </summary>
    public static List<double> Ticks(double min, double max, double step)
    {
        var result = new List<double>();
        if (step <= 0) return result;
        // Snap to the step grid to avoid 0.30000000000000004-style labels.
        var start = Math.Ceiling(min / step - 1e-9) * step;
        for (var v = start; v <= max + step * 1e-9; v += step)
            result.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
        return result;
    }

    /// <summary>Compact display label for an axis value (1.5K / 2.3M style for large magnitudes).</summary>
    public static string FormatNumber(double v)
    {
        var abs = Math.Abs(v);
        return abs switch {
            >= 1_000_000_000 => Trim(v / 1_000_000_000) + "B",
            >= 1_000_000 => Trim(v / 1_000_000) + "M",
            >= 1_000 => Trim(v / 1_000) + "K",
            _ => Trim(v),
        };

        static string Trim(double x)
        {
            // Invariant culture — axis labels must not follow the machine's decimal separator.
            return Math.Abs(x - Math.Round(x)) < 1e-9
                ? Math.Round(x).ToString("0", CultureInfo.InvariantCulture)
                : x.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
