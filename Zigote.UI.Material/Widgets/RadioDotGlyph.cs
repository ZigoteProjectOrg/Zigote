namespace Zigote.UI.Material;

/// <summary>
///     The filled centre dot of a selected radio button. The entrance/exit animation and all the
///     plumbing live in <see cref="ToggleGlyph" />; this is only the geometry.
/// </summary>
public sealed class RadioDotGlyph() : ToggleGlyph(Motion.Fast)
{
    protected override void PaintGlyph(PaintList paint, float t)
    {
        float radius = MathF.Min(x: Bounds.Width, y: Bounds.Height) / 2f;
        float inner = radius * 2f * 0.28f * MathF.Max(
            x: 0f,
            y: t
        ); // scale the dot about the centre
        var dot = new Rect(
            x: Bounds.X + radius - inner,
            y: Bounds.Y + radius - inner,
            width: inner * 2f,
            height: inner * 2f
        );
        paint.AddRect(
            bounds: dot,
            color: Color.WithAlpha(Math.Clamp(value: t, min: 0f, max: 1f)),
            radius: inner
        );
    }
}
