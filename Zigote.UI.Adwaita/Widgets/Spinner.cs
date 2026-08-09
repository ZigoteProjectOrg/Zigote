using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwSpinner — the GNOME indeterminate spinner: a neutral (dim foreground) arc whose sweep
///     grows and shrinks as it revolves. Runs on a self-owned <see cref="AnimationController" />,
///     no manual ticking required. Size is capped at 64 px like libadwaita's.
/// </summary>
public class AdwSpinner : Widget
{
    private const float MaxSize = 64f;

    private readonly AnimationController _anim;
    private Size _box;
    private ThemeData _theme = ThemeData.Dark;

    public AdwSpinner(float size = 32f)
    {
        Size = size;
        // No vsync here on purpose: a Ticker joins a static running list the moment it starts, so a
        // spinner that is built and dropped without ever being attached (a retained AdwViewStack
        // page, a faded-out transition child) would pin the frame loop awake forever. Repeat() only
        // parks the status at Forward; Attach's AttachTicker is what actually starts it.
        _anim = new AnimationController(1.2f) { Curve = Curves.Linear };
        _anim.OnTick += MarkNeedsPaint;
        _anim.Repeat();
    }

    /// <summary>Diameter in logical pixels (drawn capped at 64).</summary>
    public float Size { get; init; }


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        _anim.AttachTicker(this);
    }


    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var s = MathF.Min(Size, MaxSize);
        _box = c.Constrain(new Size(s, s));
        return _box;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _box.Width,
            _box.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        var s = MathF.Min(MathF.Min(Bounds.Width, Bounds.Height), MaxSize);
        if (s <= 2f) return;

        var stroke = MathF.Max(1.5f, 2.5f * s / 32f);
        var ring = (s - stroke) / 2f;
        if (ring <= 0f) return;

        var cx = Bounds.X + Bounds.Width / 2f;
        var cy = Bounds.Y + Bounds.Height / 2f;
        var color = _theme.Label2;

        // The faint full track libadwaita draws behind the arc.
        StrokeArc(
            paint,
            cx,
            cy,
            ring,
            0f,
            MathF.Tau,
            color.WithAlpha(color.A * 0.15f),
            stroke
        );

        // Both ends of the arc advance monotonically: the head is eased over the first half of the
        // cycle and the tail over the second, so the arc grows to half a circle and shrinks back
        // without either end ever travelling backwards (a tail that outruns its head is what made
        // the old breathing-sweep version look like it stuttered in reverse). The base rotation is
        // the rest of the revolution, so each end advances exactly one turn per cycle — continuous
        // across the Repeat wrap.
        const float span = MathF.Tau * 0.5f;
        var t = _anim.Progress;
        var basis = t * (MathF.Tau - span) - MathF.PI / 2f;
        var tail = basis + span * Smooth(Math.Clamp(t * 2f - 1f, 0f, 1f));
        var head = basis + span * Smooth(Math.Clamp(t * 2f, 0f, 1f)) + MathF.Tau * 0.02f;

        StrokeArc(
            paint,
            cx,
            cy,
            ring,
            tail,
            head,
            color,
            stroke
        );
    }

    private static float Smooth(float x)
    {
        return x * x * (3f - 2f * x);
    }

    /// <summary>
    ///     Stroke a circular arc as cubic Béziers (≤90° each, the standard 4/3·tan(θ/4) handle
    ///     length) — one anti-aliased native ribbon per segment, where the previous stamped-dot
    ///     approximation beaded and blended unevenly.
    /// </summary>
    private static void StrokeArc(PaintList paint, float cx, float cy, float r,
        float from, float to, Color color, float width)
    {
        var total = to - from;
        if (MathF.Abs(total) < 1e-4f) return;

        var segments = (int)MathF.Ceiling(MathF.Abs(total) / (MathF.PI / 2f));
        var step = total / segments;
        var k = 4f / 3f * MathF.Tan(step / 4f);

        for (var i = 0; i < segments; i++)
        {
            var a0 = from + step * i;
            var a1 = a0 + step;
            float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0);
            float c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);
            paint.AddBezier(
                cx + c0 * r,
                cy + s0 * r,
                cx + (c0 - k * s0) * r,
                cy + (s0 + k * c0) * r,
                cx + (c1 + k * s1) * r,
                cy + (s1 - k * c1) * r,
                cx + c1 * r,
                cy + s1 * r,
                color,
                width
            );
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(Size, _anim.Progress);
    }
}