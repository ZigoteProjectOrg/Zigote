using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     A single monochrome icon glyph from the bundled Material Icons face (see <see cref="Icons" />).
///     Lays out as a square box of <see cref="SizePx" /> and paints the glyph centered. Pass a glyph
///     constant from <see cref="Icons" /> (e.g. <c>Icons.Move</c>). Distinct from <see cref="Icon" />,
///     which samples a sprite atlas — this draws a vector font glyph and scales crisply at any size.
/// </summary>
public sealed class IconGlyph : Widget
{
    private readonly string _glyph;
    private Size _box;
    private ThemeData _theme = ThemeData.Dark;

    public IconGlyph(string glyph, float size = 16f, Color? color = null)
    {
        _glyph = glyph;
        SizePx = size;
        Color = color;
    }

    /// <summary>Icon size in logical pixels (the glyph is drawn on a square em of this size).</summary>
    public float SizePx { get; set; }

    /// <summary>Tint colour; <c>null</c> falls back to the theme's primary label colour.</summary>
    public Color? Color { get; set; }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _box = c.Constrain(new Size(SizePx, SizePx));
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
        Icons.Draw(
            paint,
            _glyph,
            Bounds,
            Color ?? _theme.OnSurface,
            SizePx
        );
    }
}
