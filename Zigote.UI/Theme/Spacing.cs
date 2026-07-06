namespace Zigote.UI.Theme;

/// <summary>
///     The spacing scale — an 8-point-derived progression used for padding, gaps and insets across
///     the whole framework. Prefer a named step over a magic number so layouts stay on the grid and
///     read consistently. Values are in logical pixels.
/// </summary>
public static class Spacing
{
    /// <summary>2 — hairline gaps, icon/label kerning.</summary>
    public const float Xxs = 2f;

    /// <summary>4 — tight inner padding.</summary>
    public const float Xs = 4f;

    /// <summary>8 — default gap between related controls.</summary>
    public const float Sm = 8f;

    /// <summary>12 — control inner padding, list-row insets.</summary>
    public const float Md = 12f;

    /// <summary>16 — section padding, card insets.</summary>
    public const float Lg = 16f;

    /// <summary>20 — generous section spacing.</summary>
    public const float Xl = 20f;

    /// <summary>24 — major group separation.</summary>
    public const float Xxl = 24f;

    /// <summary>32 — page-level margins.</summary>
    public const float Xxxl = 32f;
}