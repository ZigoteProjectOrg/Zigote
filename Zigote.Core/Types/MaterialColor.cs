namespace Zigote.Core;

/// <summary>
///     A Material colour swatch — a primary shade (500) plus the 50…900 tonal range, indexable
///     as <c>Colors.Blue[700]</c>. Implicitly converts to its primary <see cref="Color" />
///     ,
///     so <c>Colors.Blue</c> is usable anywhere a <see cref="Color" /> is expected.
///     <para>
///         Shades supplied explicitly (grey) are the canonical Material values; the rest are generated
///         by lightening/darkening the primary, so <c>Colors.Blue[300]</c> is visually close to
///         the canonical swatch but not bit-exact. Use a named theme token when an exact tone matters.
///     </para>
/// </summary>
public sealed class MaterialColor
{
    // Canonical Material shade stops, low→high.
    private static readonly int[] Stops = [50, 100, 200, 300, 400, 500, 600, 700, 800, 900];

    // Lighten (>0) / darken (<0) factor per stop, anchored at 500 = 0.
    private static readonly float[] Factors =
        [0.86f, 0.72f, 0.5f, 0.3f, 0.14f, 0f, -0.12f, -0.26f, -0.42f, -0.56f];

    private readonly Color[] _shades;

    public MaterialColor(Color primary, Color[]? shades = null)
    {
        Primary = primary;
        _shades = shades ?? Generate(primary);
    }

    /// <summary>The 500 shade — the swatch's representative colour.</summary>
    public Color Primary { get; }

    /// <summary>Indexer over the Material shade stops (50, 100, … 900), like <c>Colors.blue[700]</c>.</summary>
    public Color this[int shade]
    {
        get
        {
            var best = 0;
            var bestDist = int.MaxValue;
            for (var i = 0; i < Stops.Length; i++)
            {
                var d = Math.Abs(Stops[i] - shade);
                if (d >= bestDist) continue;
                bestDist = d;
                best = i;
            }

            return _shades[best];
        }
    }

    public Color Shade50 => _shades[0];
    public Color Shade100 => _shades[1];
    public Color Shade200 => _shades[2];
    public Color Shade300 => _shades[3];
    public Color Shade400 => _shades[4];
    public Color Shade500 => _shades[5];
    public Color Shade600 => _shades[6];
    public Color Shade700 => _shades[7];
    public Color Shade800 => _shades[8];
    public Color Shade900 => _shades[9];

    private static Color[] Generate(Color primary)
    {
        var shades = new Color[Stops.Length];
        for (var i = 0; i < Stops.Length; i++)
        {
            var f = Factors[i];
            shades[i] = f >= 0f ? primary.Lighten(f) : primary.Darken(-f);
        }

        return shades;
    }

    public static implicit operator Color(MaterialColor m)
    {
        return m.Primary;
    }
}
