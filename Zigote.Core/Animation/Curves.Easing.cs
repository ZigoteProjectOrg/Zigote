namespace Zigote.Core.Animation;

/// <summary>
///     The rest of the easing families — the same set CSS, Adwaita and the usual easing cheat-sheets
///     name — plus the combinators that build a curve out of other curves.
///     <para>
///         The short names in <c>Curves.cs</c> (<see cref="EaseIn" />, <see cref="EaseOut" />,
///         <see cref="EaseInOut" />) are the quadratic family and stay what they always were; the
///         names here spell their family out. Every function takes t in [0,1] and returns a value
///         that starts at 0 and ends at 1.
///     </para>
/// </summary>
public static partial class Curves
{
    // ── Cubic ─────────────────────────────────────────────────────────────────

    public static float EaseInCubic(float t) => t * t * t;

    public static float EaseOutCubic(float t) => 1f - Cube(1f - t);

    public static float EaseInOutCubic(float t) =>
        t < 0.5f ? 4f * t * t * t : 1f - (Cube((-2f * t) + 2f) / 2f);

    // ── Quartic ───────────────────────────────────────────────────────────────

    public static float EaseInQuart(float t) => t * t * t * t;

    public static float EaseOutQuart(float t) => 1f - Pow4(1f - t);

    public static float EaseInOutQuart(float t) =>
        t < 0.5f ? 8f * Pow4(t) : 1f - (Pow4((-2f * t) + 2f) / 2f);

    // ── Quintic ───────────────────────────────────────────────────────────────

    public static float EaseInQuint(float t) => Pow4(t) * t;

    public static float EaseOutQuint(float t) => 1f - (Pow4(1f - t) * (1f - t));

    public static float EaseInOutQuint(float t) =>
        t < 0.5f
            ? 16f * Pow4(t) * t
            : 1f - (Pow4((-2f * t) + 2f) * ((-2f * t) + 2f) / 2f);

    // ── Sine ──────────────────────────────────────────────────────────────────

    public static float EaseInSine(float t) => 1f - MathF.Cos(t * MathF.PI / 2f);

    public static float EaseOutSine(float t) => MathF.Sin(t * MathF.PI / 2f);

    public static float EaseInOutSine(float t) => -(MathF.Cos(MathF.PI * t) - 1f) / 2f;

    // ── Exponential ───────────────────────────────────────────────────────────

    public static float EaseInExpo(float t) => t <= 0f ? 0f : MathF.Pow(x: 2f, y: (10f * t) - 10f);

    public static float EaseOutExpo(float t) => t >= 1f ? 1f : 1f - MathF.Pow(x: 2f, y: -10f * t);

    public static float EaseInOutExpo(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t < 0.5f
            ? MathF.Pow(x: 2f, y: (20f * t) - 10f) / 2f
            : (2f - MathF.Pow(x: 2f, y: (-20f * t) + 10f)) / 2f;
    }

    // ── Circular ──────────────────────────────────────────────────────────────

    public static float EaseInCirc(float t) => 1f - MathF.Sqrt(MathF.Max(x: 0f, y: 1f - (t * t)));

    public static float EaseOutCirc(float t) =>
        MathF.Sqrt(MathF.Max(x: 0f, y: 1f - Sq(t - 1f)));

    public static float EaseInOutCirc(float t)
    {
        return t < 0.5f
            ? (1f - MathF.Sqrt(MathF.Max(x: 0f, y: 1f - Sq(2f * t)))) / 2f
            : (MathF.Sqrt(MathF.Max(x: 0f, y: 1f - Sq((-2f * t) + 2f))) + 1f) / 2f;
    }

    // ── Back (overshoots past the target) ─────────────────────────────────────

    public static float EaseInBack(float t)
    {
        const float c1 = 1.70158f;
        return ((c1 + 1f) * t * t * t) - (c1 * t * t);
    }

    public static float EaseInOutBack(float t)
    {
        const float c2 = 1.70158f * 1.525f;
        return t < 0.5f
            ? Sq(2f * t) * (((c2 + 1f) * 2f * t) - c2) / 2f
            : ((Sq((2f * t) - 2f) * (((c2 + 1f) * ((t * 2f) - 2f)) + c2)) + 2f) / 2f;
    }

    // ── Elastic ───────────────────────────────────────────────────────────────

    public static float EaseInElastic(float t)
    {
        if (t is 0f or 1f) return t;
        const float c4 = 2f * MathF.PI / 3f;
        return -MathF.Pow(x: 2f, y: (10f * t) - 10f) * MathF.Sin(((t * 10f) - 10.75f) * c4);
    }

    public static float EaseInOutElastic(float t)
    {
        if (t is 0f or 1f) return t;
        const float c5 = 2f * MathF.PI / 4.5f;
        float sine = MathF.Sin(((20f * t) - 11.125f) * c5);
        return t < 0.5f
            ? -(MathF.Pow(x: 2f, y: (20f * t) - 10f) * sine) / 2f
            : (MathF.Pow(x: 2f, y: (-20f * t) + 10f) * sine / 2f) + 1f;
    }

    // ── Bounce ────────────────────────────────────────────────────────────────

    public static float EaseInBounce(float t) => 1f - BounceOut(1f - t);

    public static float EaseInOutBounce(float t) =>
        t < 0.5f
            ? (1f - BounceOut(1f - (2f * t))) / 2f
            : (1f + BounceOut((2f * t) - 1f)) / 2f;

    // ── CSS named curves ──────────────────────────────────────────────────────

    /// <summary>The CSS <c>ease</c> keyword — <c>cubic-bezier(0.25, 0.1, 0.25, 1)</c>.</summary>
    public static float Ease(float t) => CssEase(t);

    // ── Combinators ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Run <paramref name="curve" /> over the sub-window [<paramref name="begin" />,
    ///     <paramref name="end" />] of the timeline, holding 0 before it and 1 after — the primitive
    ///     staggering is built from (child <c>i</c> gets the window that starts at <c>i · step</c>).
    /// </summary>
    public static Func<float, float> Interval(float begin, float end,
        Func<float, float>? curve = null)
    {
        float span = MathF.Max(x: end - begin, y: 1e-6f);
        var inner = curve ?? Linear;
        return t => inner(Math.Clamp(value: (t - begin) / span, min: 0f, max: 1f));
    }

    /// <summary>
    ///     The mirror image of a curve: <c>1 − c(1 − t)</c>. An ease-in becomes an ease-out, which is
    ///     what an exit animation wants when the entrance used the original.
    /// </summary>
    public static Func<float, float> Flipped(Func<float, float> curve) => t => 1f - curve(1f - t);

    /// <summary>Feed one curve's output into another — <c>b(a(t))</c>.</summary>
    public static Func<float, float> Chain(Func<float, float> a, Func<float, float> b) =>
        t => b(a(t));

    /// <summary>
    ///     A cubic Bézier easing given its two control points, exactly as CSS
    ///     <c>cubic-bezier(x1, y1, x2, y2)</c> and every design tool hand it over. The x for a given
    ///     t is solved by Newton with a bisection fallback, so control points that make x
    ///     non-uniform still map correctly.
    /// </summary>
    public static Func<float, float> Bezier(float x1, float y1, float x2, float y2)
    {
        // Polynomial coefficients of the 1-D Bézier through (0, p1, p2, 1).
        float cx = 3f * x1, bx = (3f * (x2 - x1)) - cx, ax = 1f - cx - bx;
        float cy = 3f * y1, by = (3f * (y2 - y1)) - cy, ay = 1f - cy - by;

        float SampleX(float t) => ((((ax * t) + bx) * t) + cx) * t;
        float SampleY(float t) => ((((ay * t) + by) * t) + cy) * t;
        float SlopeX(float t) => (((3f * ax * t) + (2f * bx)) * t) + cx;

        return x =>
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;

            float t = x;
            for (int i = 0; i < 8; i++)
            {
                float error = SampleX(t) - x;
                if (MathF.Abs(error) < 1e-5f) return SampleY(t);
                float slope = SlopeX(t);
                if (MathF.Abs(slope) < 1e-6f) break;
                t -= error / slope;
            }

            // Newton stalled (a flat or looping x segment): fall back to bisection, which always
            // converges because x is monotonic for control points in [0,1].
            float lo = 0f, hi = 1f;
            t = x;
            for (int i = 0; i < 20; i++)
            {
                float sample = SampleX(t);
                if (MathF.Abs(sample - x) < 1e-5f) break;
                if (sample < x) lo = t;
                else hi = t;
                t = (lo + hi) / 2f;
            }

            return SampleY(t);
        };
    }

    private static readonly Func<float, float> CssEase = Bezier(
        x1: 0.25f,
        y1: 0.1f,
        x2: 0.25f,
        y2: 1f
    );

    private static float Sq(float v) => v * v;

    private static float Cube(float v) => v * v * v;

    private static float Pow4(float v)
    {
        float s = v * v;
        return s * s;
    }
}
