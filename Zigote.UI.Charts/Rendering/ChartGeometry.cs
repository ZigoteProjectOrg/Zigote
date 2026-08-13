namespace Zigote.UI.Charts.Rendering;

/// <summary>One cubic Bézier span in screen coordinates — the unit the native stroke consumes.</summary>
public struct CubicSegment
{
    public float X0, Y0, X1, Y1, X2, Y2, X3, Y3;

    public CubicSegment(float x0, float y0, float x1, float y1, float x2, float y2, float x3,
        float y3)
    {
        X0 = x0;
        Y0 = y0;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        X3 = x3;
        Y3 = y3;
    }

    /// <summary>A straight segment expressed as a cubic (handles at the 1/3 points).</summary>
    public static CubicSegment Line(float x0, float y0, float x1, float y1)
    {
        float dx = (x1 - x0) / 3f;
        float dy = (y1 - y0) / 3f;
        return new CubicSegment(
            x0: x0,
            y0: y0,
            x1: x0 + dx,
            y1: y0 + dy,
            x2: x1 - dx,
            y2: y1 - dy,
            x3: x1,
            y3: y1
        );
    }
}

/// <summary>
///     Pure curve math for chart strokes: monotone-cubic interpolation (Fritsch–Carlson — never
///     overshoots the data), Hermite→Bézier conversion, arc
///     approximation for sectors, and dash segmentation. All headless-testable.
/// </summary>
public static class ChartGeometry
{
    // Thread-local (not static) so parallel headless tests never share a buffer.
    [ThreadStatic] private static float[]? _slopeScratch;

    /// <summary>
    ///     Per-point slopes (dy/dx) for a monotone cubic through (<paramref name="xs" />,
    ///     <paramref name="ys" />). The Fritsch–Carlson limiter guarantees the curve never overshoots
    ///     between samples — essential for data plots.
    /// </summary>
    public static float[] MonotoneSlopes(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys)
    {
        float[] m = new float[xs.Length];
        MonotoneSlopes(xs: xs, ys: ys, m: m);
        return m;
    }

    /// <summary>
    ///     <see cref="MonotoneSlopes(ReadOnlySpan{float}, ReadOnlySpan{float})" /> into a reused
    ///     thread-local scratch — zero-alloc steady state for the per-frame stroke path. The span is
    ///     only valid until the next call on the same thread.
    /// </summary>
    internal static ReadOnlySpan<float> MonotoneSlopesScratch(ReadOnlySpan<float> xs,
        ReadOnlySpan<float> ys)
    {
        float[]? buf = _slopeScratch;
        if (buf is null || buf.Length < xs.Length)
            _slopeScratch = buf = new float[Math.Max(val1: xs.Length, val2: 256)];
        var m = buf.AsSpan(start: 0, length: xs.Length);
        MonotoneSlopes(xs: xs, ys: ys, m: m);
        return m;
    }

    /// <summary>Fritsch–Carlson monotone slopes written into <paramref name="m" /> (reused buffer).</summary>
    public static void MonotoneSlopes(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, Span<float> m)
    {
        int n = xs.Length;
        if (n < 2)
        {
            m[..n].Clear();
            return;
        }

        var delta = n <= 256 ? stackalloc float[n - 1] : new float[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            float dx = xs[i + 1] - xs[i];
            delta[i] = dx > 1e-6f ? (ys[i + 1] - ys[i]) / dx : 0f;
        }

        m[0] = delta[0];
        m[n - 1] = delta[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            m[i] = Math.Sign(delta[i - 1]) != Math.Sign(delta[i]) || delta[i - 1] == 0 ||
                   delta[i] == 0
                ? 0f
                : (delta[i - 1] + delta[i]) / 2f;
        }

        // Limit slopes so no segment overshoots.
        for (int i = 0; i < n - 1; i++)
        {
            if (delta[i] == 0f)
            {
                m[i] = 0f;
                m[i + 1] = 0f;
                continue;
            }

            float a = m[i] / delta[i];
            float b = m[i + 1] / delta[i];
            float s = (a * a) + (b * b);
            if (s > 9f)
            {
                float t = 3f / MathF.Sqrt(s);
                m[i] = t * a * delta[i];
                m[i + 1] = t * b * delta[i];
            }
        }
    }

    /// <summary>Hermite segment (positions + slopes at both ends) as a cubic Bézier.</summary>
    public static CubicSegment HermiteToCubic(float x0, float y0, float m0, float x1, float y1,
        float m1)
    {
        float dx = (x1 - x0) / 3f;
        return new CubicSegment(
            x0: x0,
            y0: y0,
            x1: x0 + dx,
            y1: y0 + (m0 * dx),
            x2: x1 - dx,
            y2: y1 - (m1 * dx),
            x3: x1,
            y3: y1
        );
    }

    /// <summary>
    ///     Sample the monotone cubic defined by (<paramref name="xs" />, <paramref name="ys" />,
    ///     <paramref name="slopes" />) at <paramref name="x" />. Clamps outside the domain. Used by
    ///     area fills so the fill edge follows the exact stroked curve.
    /// </summary>
    public static float EvaluateMonotone(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys,
        ReadOnlySpan<float> slopes, float x)
    {
        int n = xs.Length;
        if (n == 0) return 0f;
        if (n == 1 || x <= xs[0]) return ys[0];
        if (x >= xs[n - 1]) return ys[n - 1];

        // Binary search for the bracketing segment.
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (xs[mid] <= x) lo = mid;
            else hi = mid;
        }

        float h = xs[hi] - xs[lo];
        if (h <= 1e-6f) return ys[lo];
        float t = (x - xs[lo]) / h;
        float t2 = t * t;
        float t3 = t2 * t;
        float h00 = (2 * t3) - (3 * t2) + 1;
        float h10 = t3 - (2 * t2) + t;
        float h01 = (-2 * t3) + (3 * t2);
        float h11 = t3 - t2;
        return (h00 * ys[lo]) + (h10 * h * slopes[lo]) + (h01 * ys[hi]) + (h11 * h * slopes[hi]);
    }

    /// <summary>Linear interpolation along a polyline at <paramref name="x" /> (clamped at the ends).</summary>
    public static float EvaluateLinear(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, float x)
    {
        int n = xs.Length;
        if (n == 0) return 0f;
        if (n == 1 || x <= xs[0]) return ys[0];
        if (x >= xs[n - 1]) return ys[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (xs[mid] <= x) lo = mid;
            else hi = mid;
        }

        float h = xs[hi] - xs[lo];
        if (h <= 1e-6f) return ys[lo];
        float t = (x - xs[lo]) / h;
        return ys[lo] + ((ys[hi] - ys[lo]) * t);
    }

    /// <summary>
    ///     Approximate a circular arc with cubic Béziers, one per ≤90° span. Angles are radians,
    ///     measured clockwise from 12 o'clock (chart convention: 0 = up, π/2 = right in a y-down
    ///     coordinate system).
    /// </summary>
    public static List<CubicSegment> ArcToCubics(float cx, float cy, float radius, float startAngle,
        float endAngle)
    {
        var segments = new List<CubicSegment>();
        float sweep = endAngle - startAngle;
        if (MathF.Abs(sweep) < 1e-5f || radius <= 0f) return segments;

        int parts = Math.Max(val1: 1, val2: (int)MathF.Ceiling(MathF.Abs(sweep) / (MathF.PI / 2f)));
        float step = sweep / parts;
        // Standard magic-number tangent length for a cubic arc span.
        float k = 4f / 3f * MathF.Tan(step / 4f);

        for (int i = 0; i < parts; i++)
        {
            float a0 = startAngle + (step * i);
            float a1 = a0 + step;
            (float sx, float sy) = Point(a0);
            (float ex, float ey) = Point(a1);
            // Tangents are perpendicular to the radius (rotated +90° along the sweep direction).
            (float tx0, float ty0) = Tangent(a0);
            (float tx1, float ty1) = Tangent(a1);
            segments.Add(
                new CubicSegment(
                    x0: sx,
                    y0: sy,
                    x1: sx + (tx0 * k * radius),
                    y1: sy + (ty0 * k * radius),
                    x2: ex - (tx1 * k * radius),
                    y2: ey - (ty1 * k * radius),
                    x3: ex,
                    y3: ey
                )
            );
        }

        return segments;

        (float, float) Point(float a) =>
            (cx + (MathF.Sin(a) * radius), cy - (MathF.Cos(a) * radius));

        (float, float) Tangent(float a) => (MathF.Cos(a), MathF.Sin(a));
    }

    /// <summary>Angle-of and position-on helpers shared by sector paint + hit-testing.</summary>
    public static (float X, float Y) PolarPoint(float cx, float cy, float radius, float angle) => (
        cx + (MathF.Sin(angle) * radius), cy - (MathF.Cos(angle) * radius));

    /// <summary>
    ///     Largest-Triangle-Three-Buckets downsample of a screen-space polyline to
    ///     <paramref name="threshold" /> points, preserving the visual shape (peaks/troughs) far
    ///     better than uniform striding. Returns the selected indices (ascending, endpoints kept).
    ///     Input x must be ascending. When the series already fits, returns <c>null</c> (use as-is).
    /// </summary>
    public static int[]? LttbIndices(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, int threshold)
    {
        int n = xs.Length;
        if (threshold < 3 || n <= threshold) return null;

        int[] sampled = new int[threshold];
        sampled[0] = 0;
        float bucketSize = (float)(n - 2) / (threshold - 2);
        int a = 0; // last selected point

        for (int i = 0; i < threshold - 2; i++)
        {
            // Average point of the next bucket (the triangle's far vertex).
            int avgStart = (int)MathF.Floor((i + 1) * bucketSize) + 1;
            int avgEnd = Math.Min(val1: (int)MathF.Floor((i + 2) * bucketSize) + 1, val2: n);
            float avgX = 0, avgY = 0;
            int count = Math.Max(val1: 1, val2: avgEnd - avgStart);
            for (int j = avgStart; j < avgEnd; j++)
            {
                avgX += xs[j];
                avgY += ys[j];
            }

            avgX /= count;
            avgY /= count;

            // Point in this bucket forming the largest triangle with a and the next bucket average.
            int rangeStart = (int)MathF.Floor(i * bucketSize) + 1;
            int rangeEnd = (int)MathF.Floor((i + 1) * bucketSize) + 1;
            float ax = xs[a];
            float ay = ys[a];
            float maxArea = -1f;
            int chosen = rangeStart;
            for (int j = rangeStart; j < rangeEnd && j < n; j++)
            {
                float area =
                    MathF.Abs(((ax - avgX) * (ys[j] - ay)) - ((ax - xs[j]) * (avgY - ay))) *
                    0.5f;
                if (area > maxArea)
                {
                    maxArea = area;
                    chosen = j;
                }
            }

            sampled[i + 1] = chosen;
            a = chosen;
        }

        sampled[threshold - 1] = n - 1;
        return sampled;
    }

    /// <summary>Split [0,<paramref name="length" />] into painted dash intervals.</summary>
    public static IEnumerable<(float Start, float End)> Dashes(float length, float dash, float gap)
    {
        if (dash <= 0f || length <= 0f)
        {
            if (length > 0f) yield return (0f, length);
            yield break;
        }

        float pos = 0f;
        while (pos < length)
        {
            float end = MathF.Min(x: pos + dash, y: length);
            yield return (pos, end);
            pos = end + gap;
        }
    }
}
