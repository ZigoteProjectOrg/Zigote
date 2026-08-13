namespace Zigote.UI.Charts.Marks;

/// <summary>
///     One (x, y) pair produced by the vectorized array factories — <c>LineMark.Of(xs, ys)</c>,
///     <c>AreaMark.Of(ys)</c>, … — which plot plain <c>double</c> arrays without a row type or
///     selector lambdas at the call site (the vectorized-plot analogue). Pairing happens once at
///     construction; the shared static selectors capture nothing.
/// </summary>
public readonly record struct ChartSample(double X, double Y);

internal static class ChartSamples
{
    internal static readonly Func<ChartSample, ChartValue> X = static s => s.X;
    internal static readonly Func<ChartSample, ChartValue> Y = static s => s.Y;

    /// <summary>Pair xs/ys into samples; an empty xs plots ys against their indices (0, 1, 2, …).</summary>
    internal static ChartSample[] Pair(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        var n = xs.IsEmpty ? ys.Length : Math.Min(xs.Length, ys.Length);
        var data = new ChartSample[n];
        for (var i = 0; i < n; i++) data[i] = new ChartSample(xs.IsEmpty ? i : xs[i], ys[i]);
        return data;
    }
}
