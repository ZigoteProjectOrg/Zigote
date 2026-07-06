using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     A monochrome icon rendered from the bundled Material Icons face. The <see cref="IconName" /> is
///     a
///     friendly alias (e.g. <c>"home"</c>, <c>"search"</c>, <c>"add"</c>) resolved to a glyph
///     codepoint;
///     unknown names fall back to a neutral "help" glyph. Scales crisply at any <see cref="Size" />
///     and
///     tints to <see cref="Color" /> (or the theme's on-surface label when null). This is the general
///     icon primitive used across the Material widgets; <see cref="IconGlyph" /> is the lower-level
///     form
///     that takes a raw glyph constant from <see cref="Icons" /> / <see cref="MaterialIcons" />.
/// </summary>
public class Icon(string iconName) : Widget
{
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public string IconName { get; set; } = iconName;
    public float Size { get; set; } = 24f;
    public Color? Color { get; set; }

    private string Glyph => IconName;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(Size, Size));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        Icons.Draw(
            paint,
            Glyph,
            Bounds,
            Color ?? _theme.OnSurface,
            Size
        );
    }
}