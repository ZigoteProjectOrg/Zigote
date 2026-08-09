namespace Zigote.UI.Material;

/// <summary>
///     The filled centre dot of a selected radio button. The entrance/exit animation and all the
///     plumbing live in <see cref="ToggleGlyph" />; this is only the geometry.
/// </summary>
public sealed class RadioDotGlyph() : ToggleGlyph(Motion.Fast)
{
    protected override void PaintGlyph(PaintList paint, float t)
    {
        var radius = MathF.Min(Bounds.Width, Bounds.Height) / 2f;
        var inner = radius * 2f * 0.28f * MathF.Max(0f, t); // scale the dot about the centre
        var dot = new Rect(
            Bounds.X + radius - inner,
            Bounds.Y + radius - inner,
            inner * 2f,
            inner * 2f
        );
        paint.AddRect(dot, Color.WithAlpha(Math.Clamp(t, 0f, 1f)), inner);
    }
}
