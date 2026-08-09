namespace Zigote.UI.Material;

/// <summary>
///     The check-mark tick, drawn as two rounded strokes inside its bounds. The entrance/exit
///     animation and all the plumbing live in <see cref="ToggleGlyph" />; this is only the geometry.
/// </summary>
public sealed class CheckGlyph() : ToggleGlyph(Motion.Standard)
{
    protected override void PaintGlyph(PaintList paint, float t)
    {
        var s = MathF.Min(Bounds.Width, Bounds.Height);
        var stroke = MathF.Max(1.5f, s * 0.12f);
        var color = Color.WithAlpha(Math.Clamp(t, 0f, 1f));

        // Scale the tick about the glyph centre so it pops in and eases out.
        var mx = Bounds.X + Bounds.Width / 2f;
        var my = Bounds.Y + Bounds.Height / 2f;

        // Geometry of a tick: short leg from the lower-left, long leg up to the upper-right.
        var cx = Bounds.X;
        var cy = Bounds.Y;
        var ax = cx + s * 0.26f;
        var ay = cy + s * 0.52f;
        var bx = cx + s * 0.42f;
        var by = cy + s * 0.68f;
        var dx = cx + s * 0.74f;
        var dy = cy + s * 0.34f;

        StrokeLine(
            paint,
            mx + (ax - mx) * t,
            my + (ay - my) * t,
            mx + (bx - mx) * t,
            my + (by - my) * t,
            stroke,
            color
        );
        StrokeLine(
            paint,
            mx + (bx - mx) * t,
            my + (by - my) * t,
            mx + (dx - mx) * t,
            my + (dy - my) * t,
            stroke,
            color
        );
    }

    /// <summary>Approximates a short line with a chain of small square dabs (no native line primitive).</summary>
    private static void StrokeLine(PaintList paint, float x0, float y0, float x1, float y1, float w,
        Color color)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        var steps = MathF.Max(1f, MathF.Ceiling(len / (w * 0.4f)));
        var half = w / 2f;

        for (var i = 0f; i <= steps; i++)
        {
            var t = i / steps;
            var px = x0 + dx * t;
            var py = y0 + dy * t;
            paint.AddRect(
                new Rect(
                    px - half,
                    py - half,
                    w,
                    w
                ),
                color,
                half
            );
        }
    }
}
