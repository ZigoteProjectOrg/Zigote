using Zigote.UI.Theme;

namespace Zigote.UI.TextShaping;

/// <summary>
///     Where to put a text baseline so a line sits optically centred in a box — one rule for every
///     widget that paints text.
///     <para>
///         It centres the font's ascent+descent box inside the given height (CSS half-leading), using
///         the bundled faces' real vertical metrics. For Inter that also centres the cap height, which
///         is what the eye checks on a button label; for Material Icons (ascent 1, descent 0, glyphs
///         drawn 0.125–0.875 em above the baseline) it centres the glyph.
///     </para>
///     <para>
///         Replaces the <c>top + fontSize * 0.8</c> heuristic that was scattered across Label, menus,
///         rows and editors: in a line box of the usual 1.25 em it left 0.07 em above the capitals and
///         0.4 em below the baseline, so every label sat about 0.16 em above centre.
///     </para>
/// </summary>
public static class TextBaseline
{
    // Inter's hhea metrics (UI text): ascender 1984/2048, descender 494/2048. Iosevka (code) is
    // within 0.01 em of these, so it shares them.
    private const float TextAscent = 0.96875f;
    private const float TextDescent = 0.2412f;

    /// <summary>
    ///     Baseline for one line of <paramref name="fontSize" /> text centred vertically in
    ///     [<paramref name="top" />, <paramref name="top" /> + <paramref name="height" />]. For a line
    ///     of a multi-line block, pass the line's own top and the line height.
    /// </summary>
    public static float Centered(float top, float height, float fontSize, string? fontFamily = null)
    {
        bool icons = fontFamily == Icons.Family;
        float ascent = icons ? 1f : TextAscent;
        float descent = icons ? 0f : TextDescent;
        return top + ((height - ((ascent + descent) * fontSize)) / 2f) + (ascent * fontSize);
    }
}
