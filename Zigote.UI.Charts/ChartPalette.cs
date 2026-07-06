using Zigote.Core;
using Zigote.UI.Theme;

namespace Zigote.UI.Charts;

/// <summary>
///     Categorical series palette in the system-color order (blue, green, orange, purple, …),
///     with light/dark variants matching the flat-macOS theme. Series colors cycle when there are
///     more series than palette entries.
/// </summary>
public sealed class ChartPalette
{
    private static readonly Color[] DarkColors = [
        Color.Rgb(10, 132, 255), // blue
        Color.Rgb(48, 209, 88), // green
        Color.Rgb(255, 159, 10), // orange
        Color.Rgb(191, 90, 242), // purple
        Color.Rgb(255, 69, 58), // red
        Color.Rgb(100, 210, 255), // cyan
        Color.Rgb(255, 214, 10), // yellow
        Color.Rgb(255, 55, 95), // pink
        Color.Rgb(94, 92, 230), // indigo
        Color.Rgb(102, 212, 207), // mint
        Color.Rgb(172, 142, 104), // brown
    ];

    private static readonly Color[] LightColors = [
        Color.Rgb(0, 122, 255),
        Color.Rgb(52, 199, 89),
        Color.Rgb(255, 149, 0),
        Color.Rgb(175, 82, 222),
        Color.Rgb(255, 59, 48),
        Color.Rgb(50, 173, 230),
        Color.Rgb(255, 204, 0),
        Color.Rgb(255, 45, 85),
        Color.Rgb(88, 86, 214),
        Color.Rgb(0, 199, 190),
        Color.Rgb(162, 132, 94),
    ];

    private readonly IReadOnlyList<Color> _colors;

    public ChartPalette(IReadOnlyList<Color> colors)
    {
        _colors = colors.Count > 0 ? colors : DarkColors;
    }

    public Color this[int index] =>
        _colors[(index % _colors.Count + _colors.Count) % _colors.Count];

    public int Count => _colors.Count;

    public static ChartPalette For(ThemeData theme)
    {
        return new ChartPalette(theme.IsDark ? DarkColors : LightColors);
    }
}