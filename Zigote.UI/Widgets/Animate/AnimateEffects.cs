using Zigote.Core;
using Zigote.Core.Animation;

namespace Zigote.UI.Widgets;

/// <summary>
///     The mutable per-frame accumulator an <see cref="Animate" /> builds by folding every active
///     effect. Alpha/Scale multiply (so stacked fades/scales compose); translation sums.
/// </summary>
internal struct AnimateFrame
{
    public float Alpha;
    public float Scale;
    public float Tx;
    public float Ty;

    public static AnimateFrame Identity => new() {
        Alpha = 1f,
        Scale = 1f,
        Tx = 0f,
        Ty = 0f,
    };
}

/// <summary>
///     One step in an <see cref="Animate" /> timeline. Mirrors flutter_animate's <c>Effect</c>: an
///     optional <see cref="Delay" />/<see cref="Duration" />/<see cref="Curve" /> (each inherits from
///     the previous effect when null) plus <c>begin</c>/<c>end</c> bounds on the concrete subtype. The
///     resolved absolute window <see cref="BeginS" />..<see cref="EndS" /> (seconds) is filled in by
///     <see cref="Animate" /> during timeline resolution.
/// </summary>
public abstract class AnimateEffect
{
    public TimeSpan? Delay { get; init; }
    public TimeSpan? Duration { get; init; }
    public Func<float, float>? Curve { get; init; }

    internal float BeginS;
    internal float EndS;
    internal Func<float, float> ResolvedCurve = Curves.EaseOut;

    /// <summary>A zero-length baseline marker (<c>ThenEffect</c>) that shifts subsequent effect timing.</summary>
    internal virtual bool IsMarker => false;

    /// <summary>
    ///     Fold this effect's contribution into <paramref name="frame" />.
    ///     <paramref name="raw" /> is the linear local progress in [0,1]; <paramref name="eased" /> is
    ///     <paramref name="raw" /> through the resolved curve. <paramref name="natural" /> is the child's
    ///     unscaled size (for fractional slides).
    /// </summary>
    internal abstract void Apply(ref AnimateFrame frame, float raw, float eased, Size natural);

    private protected static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }
}

/// <summary>Cross-fades opacity. Neutral value 1 (opaque); default 0→1 (fade in).</summary>
public sealed class FadeEffect : AnimateEffect
{
    public float? Begin { get; init; }
    public float? End { get; init; }

    internal override void Apply(ref AnimateFrame frame, float raw, float eased, Size natural)
    {
        var begin = Begin ?? (End is null ? 0f : 1f);
        var end = End ?? 1f;
        frame.Alpha *= Lerp(begin, end, eased);
    }
}

/// <summary>Uniform scale about the centre. Neutral value 1; default 0→1 (scale up).</summary>
public sealed class ScaleEffect : AnimateEffect
{
    public float? Begin { get; init; }
    public float? End { get; init; }

    internal override void Apply(ref AnimateFrame frame, float raw, float eased, Size natural)
    {
        var begin = Begin ?? (End is null ? 0f : 1f);
        var end = End ?? 1f;
        frame.Scale *= Lerp(begin, end, eased);
    }
}

/// <summary>Translates by a pixel offset. Neutral value zero; default (0,24)→(0,0) (rise into place).</summary>
public sealed class MoveEffect : AnimateEffect
{
    public Offset? Begin { get; init; }
    public Offset? End { get; init; }

    internal override void Apply(ref AnimateFrame frame, float raw, float eased, Size natural)
    {
        var begin = Begin ?? (End is null ? new Offset(0f, 24f) : Offset.Zero);
        var end = End ?? Offset.Zero;
        frame.Tx += Lerp(begin.X, end.X, eased);
        frame.Ty += Lerp(begin.Y, end.Y, eased);
    }
}

/// <summary>
///     Translates by an offset expressed as a fraction of the widget's own size (like flutter_animate's
///     <c>SlideEffect</c>). Neutral value zero; default (0,-0.25)→(0,0) (slide down from above).
/// </summary>
public sealed class SlideEffect : AnimateEffect
{
    public Offset? Begin { get; init; }
    public Offset? End { get; init; }

    internal override void Apply(ref AnimateFrame frame, float raw, float eased, Size natural)
    {
        var begin = Begin ?? (End is null ? new Offset(0f, -0.25f) : Offset.Zero);
        var end = End ?? Offset.Zero;
        frame.Tx += Lerp(begin.X, end.X, eased) * natural.Width;
        frame.Ty += Lerp(begin.Y, end.Y, eased) * natural.Height;
    }
}

/// <summary>
///     Oscillating positional shake (attention-getter). Runs <see cref="Hz" /> cycles per second over
///     the effect's duration, damping to rest at the end. Uses raw (un-eased) progress.
/// </summary>
public sealed class ShakeEffect : AnimateEffect
{
    public float Hz { get; init; } = 8f;

    /// <summary>Peak displacement in pixels.</summary>
    public Offset Amount { get; init; } = new(6f, 0f);

    internal override void Apply(ref AnimateFrame frame, float raw, float eased, Size natural)
    {
        var durS = MathF.Max(0.0001f, EndS - BeginS);
        var phase = raw * durS * Hz * MathF.Tau;
        var damp = 1f - raw; // taper to zero so it settles cleanly
        var wave = MathF.Sin(phase) * damp;
        frame.Tx += Amount.X * wave;
        frame.Ty += Amount.Y * wave;
    }
}

/// <summary>
///     A zero-length baseline marker. After a <c>Then</c>, subsequent effects with no explicit delay
///     start where the previous effect ended (flutter_animate's <c>ThenEffect</c>). Contributes nothing
///     visually.
/// </summary>
public sealed class ThenEffect : AnimateEffect
{
    internal override bool IsMarker => true;

    internal override void Apply(ref AnimateFrame frame, float raw, float eased, Size natural)
    {
    }
}