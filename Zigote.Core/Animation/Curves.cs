namespace Zigote.Core.Animation;

/// <summary>Easing curve functions. Each maps a normalized t in [0,1] to [0,1].</summary>
public static class Curves
{
    public static float Linear(float t) => t;

    public static float EaseIn(float t) => t * t;

    public static float EaseOut(float t) => 1f - ((1f - t) * (1f - t));

    public static float EaseInOut(float t) =>
        t < 0.5f ? 2f * t * t : 1f - (MathF.Pow(x: (-2f * t) + 2f, y: 2f) / 2f);

    public static float BounceOut(float t)
    {
        const float n1 = 7.5625f, d1 = 2.75f;
        return t switch {
            < 1f / d1 => n1 * t * t,
            < 2f / d1 => (n1 * (t -= 1.5f / d1) * t) + 0.75f,
            < 2.5f / d1 => (n1 * (t -= 2.25f / d1) * t) + 0.9375f,
            _ => (n1 * (t -= 2.625f / d1) * t) + 0.984375f,
        };
    }

    public static float ElasticOut(float t)
    {
        if (t is 0f or 1f) return t;
        return (MathF.Pow(x: 2f, y: -10f * t) *
                MathF.Sin(((t * 10f) - 0.75f) * (2f * MathF.PI / 3f))) + 1f;
    }

    /// <summary>
    ///     An under-damped spring response with a small overshoot then settle — the motion macOS
    ///     leans on for most transitions. Normalised so f(0)=0 and f(1)≈1.
    /// </summary>
    public static float Spring(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        const float omega = 8f; // angular frequency
        const float zeta = 0.62f; // damping ratio (<1 → slight overshoot)
        float wd = omega * MathF.Sqrt(1f - (zeta * zeta));
        return 1f - (MathF.Exp(-zeta * omega * t) *
                     (MathF.Cos(wd * t) + (zeta * omega / wd * MathF.Sin(wd * t))));
    }

    /// <summary>Ease-out with a gentle overshoot past the target before settling (snappy, macOS-like).</summary>
    public static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + (c3 * u * u * u) + (c1 * u * u);
    }
}
