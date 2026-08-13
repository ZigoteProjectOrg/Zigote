namespace Zigote.UI.Material;

/// <summary>
///     The check-mark tick, drawn as two rounded strokes inside its bounds. The entrance/exit
///     animation and all the plumbing live in <see cref="ToggleGlyph" />; this is only the geometry.
/// </summary>
public sealed class CheckGlyph() : ToggleGlyph(Motion.Standard)
{
    protected override void PaintGlyph(PaintList paint, float t)
    {
        float s = MathF.Min(x: Bounds.Width, y: Bounds.Height);
        float stroke = MathF.Max(x: 1.5f, y: s * 0.12f);
        var color = Color.WithAlpha(Math.Clamp(value: t, min: 0f, max: 1f));

        // Scale the tick about the glyph centre so it pops in and eases out.
        float mx = Bounds.X + (Bounds.Width / 2f);
        float my = Bounds.Y + (Bounds.Height / 2f);

        // Geometry of a tick: short leg from the lower-left, long leg up to the upper-right.
        float cx = Bounds.X;
        float cy = Bounds.Y;
        float ax = cx + (s * 0.26f);
        float ay = cy + (s * 0.52f);
        float bx = cx + (s * 0.42f);
        float by = cy + (s * 0.68f);
        float dx = cx + (s * 0.74f);
        float dy = cy + (s * 0.34f);

        StrokeLine(
            paint: paint,
            x0: mx + ((ax - mx) * t),
            y0: my + ((ay - my) * t),
            x1: mx + ((bx - mx) * t),
            y1: my + ((by - my) * t),
            w: stroke,
            color: color
        );
        StrokeLine(
            paint: paint,
            x0: mx + ((bx - mx) * t),
            y0: my + ((by - my) * t),
            x1: mx + ((dx - mx) * t),
            y1: my + ((dy - my) * t),
            w: stroke,
            color: color
        );
    }

    /// <summary>Approximates a short line with a chain of small square dabs (no native line primitive).</summary>
    private static void StrokeLine(PaintList paint, float x0, float y0, float x1, float y1, float w,
        Color color)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float len = MathF.Sqrt((dx * dx) + (dy * dy));
        float steps = MathF.Max(x: 1f, y: MathF.Ceiling(len / (w * 0.4f)));
        float half = w / 2f;

        for (float i = 0f; i <= steps; i++)
        {
            float t = i / steps;
            float px = x0 + (dx * t);
            float py = y0 + (dy * t);
            paint.AddRect(
                bounds: new Rect(
                    x: px - half,
                    y: py - half,
                    width: w,
                    height: w
                ),
                color: color,
                radius: half
            );
        }
    }
}
