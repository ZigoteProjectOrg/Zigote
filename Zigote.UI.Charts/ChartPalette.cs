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
        Color.Rgb(r: 10, g: 132, b: 255), // blue
        Color.Rgb(r: 48, g: 209, b: 88), // green
        Color.Rgb(r: 255, g: 159, b: 10), // orange
        Color.Rgb(r: 191, g: 90, b: 242), // purple
        Color.Rgb(r: 255, g: 69, b: 58), // red
        Color.Rgb(r: 100, g: 210, b: 255), // cyan
        Color.Rgb(r: 255, g: 214, b: 10), // yellow
        Color.Rgb(r: 255, g: 55, b: 95), // pink
        Color.Rgb(r: 94, g: 92, b: 230), // indigo
        Color.Rgb(r: 102, g: 212, b: 207), // mint
        Color.Rgb(r: 172, g: 142, b: 104), // brown
    ];

    private static readonly Color[] LightColors = [
        Color.Rgb(r: 0, g: 122, b: 255),
        Color.Rgb(r: 52, g: 199, b: 89),
        Color.Rgb(r: 255, g: 149, b: 0),
        Color.Rgb(r: 175, g: 82, b: 222),
        Color.Rgb(r: 255, g: 59, b: 48),
        Color.Rgb(r: 50, g: 173, b: 230),
        Color.Rgb(r: 255, g: 204, b: 0),
        Color.Rgb(r: 255, g: 45, b: 85),
        Color.Rgb(r: 88, g: 86, b: 214),
        Color.Rgb(r: 0, g: 199, b: 190),
        Color.Rgb(r: 162, g: 132, b: 94),
    ];

    private readonly IReadOnlyList<Color> _colors;

    public ChartPalette(IReadOnlyList<Color> colors) =>
        _colors = colors.Count > 0 ? colors : DarkColors;

    public Color this[int index] =>
        _colors[((index % _colors.Count) + _colors.Count) % _colors.Count];

    public int Count => _colors.Count;

    public static ChartPalette For(ThemeData theme) => new(theme.IsDark ? DarkColors : LightColors);
}
