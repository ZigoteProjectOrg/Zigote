namespace Zigote.UI.Charts;

/// <summary>How a multi-series bar/area mark composes its series along the value axis.</summary>
public enum ChartStacking : byte
{
    /// <summary>Series overlap (bars group side-by-side instead).</summary>
    None,

    /// <summary>Positive values stack upward, negative downward, per x position.</summary>
    Standard,

    /// <summary>Each x column is rescaled so the stack spans [0,1] (100% stacked).</summary>
    Normalized,

    /// <summary>Stack centred on zero (streamgraph silhouette).</summary>
    Center,
}

/// <summary>Bottom/top extent of one stacked datum along the value axis, in data units.</summary>
public readonly record struct StackedSpan(double Bottom, double Top)
{
    public double Value => Top - Bottom;
}

/// <summary>
///     Caller-owned scratch for the stacked modes: pools the per-x column maps across
///     <see cref="StackCompute.Compute" /> calls. A live stacked chart re-resolves many times a
///     second, so without this every resolve churned a fresh dictionary per x position.
/// </summary>
public sealed class StackScratch
{
    internal readonly Dictionary<ChartValue, Dictionary<string, double>> ByX = new();
    internal readonly Stack<Dictionary<string, double>> Pool = new();

    /// <summary>Return every column map to the pool and clear the grouping for the next compute.</summary>
    internal void Recycle()
    {
        foreach (var column in ByX.Values)
        {
            column.Clear();
            Pool.Push(column);
        }

        ByX.Clear();
    }

    internal Dictionary<string, double> RentColumn() =>
        Pool.Count > 0 ? Pool.Pop() : new Dictionary<string, double>();
}

/// <summary>
///     Pure stacking math shared by <c>BarMark</c> and <c>AreaMark</c>: given (series, x, value)
///     triples and the series paint order, produce each datum's bottom/top along the value axis.
///     Positive and negative values stack in opposite directions (diverging bars come out right).
///     Keys are the raw <see cref="ChartValue" />s (value equality) — no per-point key strings —
///     and the caller supplies + reuses the <paramref name="result" /> map (plus an optional
///     <paramref name="scratch" /> for the stacked modes), so a live chart's re-resolve allocates
///     nothing steady-state.
/// </summary>
public static class StackCompute
{
    public static void Compute(
        IReadOnlyList<(string Series, ChartValue X, double Value)> points,
        IReadOnlyList<string> seriesOrder,
        ChartStacking mode,
        Dictionary<(string Series, ChartValue X), StackedSpan> result,
        StackScratch? scratch = null)
    {
        result.Clear();
        if (mode == ChartStacking.None)
        {
            for (int i = 0; i < points.Count; i++)
            {
                (string series, var key, double value) = points[i];
                result[(series, key)] =
                    value >= 0
                        ? new StackedSpan(Bottom: 0, Top: value)
                        : new StackedSpan(Bottom: value, Top: 0);
            }

            return;
        }

        // Group values by x, preserving one slot per (series, x); a duplicate datum overwrites.
        Dictionary<ChartValue, Dictionary<string, double>> byX;
        if (scratch is null)
            byX = new Dictionary<ChartValue, Dictionary<string, double>>();
        else
        {
            scratch.Recycle();
            byX = scratch.ByX;
        }

        for (int i = 0; i < points.Count; i++)
        {
            (string series, var key, double value) = points[i];
            if (!byX.TryGetValue(key: key, value: out var column))
                byX[key] = column = scratch?.RentColumn() ?? new Dictionary<string, double>();
            column[series] = value;
        }

        foreach (var (key, column) in byX)
        {
            double posSum = 0, negSum = 0, total = 0;
            foreach (double v in column.Values) total += Math.Abs(v);
            double scale = mode == ChartStacking.Normalized && total > 0 ? 1.0 / total : 1.0;
            double offset = mode == ChartStacking.Center ? -total * scale / 2.0 : 0.0;

            for (int s = 0; s < seriesOrder.Count; s++)
            {
                string series = seriesOrder[s];
                if (!column.TryGetValue(key: series, value: out double v)) continue;
                double scaled = v * scale;
                if (scaled >= 0)
                {
                    result[(series, key)] = new StackedSpan(
                        Bottom: offset + posSum,
                        Top: offset + posSum + scaled
                    );
                    posSum += scaled;
                }
                else
                {
                    result[(series, key)] = new StackedSpan(
                        Bottom: offset + negSum + scaled,
                        Top: offset + negSum
                    );
                    negSum += scaled;
                }
            }
        }
    }
}
