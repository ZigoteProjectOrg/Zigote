using System.Globalization;

namespace Zigote.UI.Material;

/// <summary>
///     Pure colour-space helpers for the <see cref="ColorPicker" /> and any other widget that needs
///     HSV ⇄ RGB conversion or hex parsing/formatting. No engine dependencies — operates only on
///     <see cref="Color" /> (f32 RGBA in [0,1]).
/// </summary>
public static class ColorMath
{
    /// <summary>
    ///     Convert an RGB colour to HSV. Hue is in degrees [0,360); saturation and value in [0,1].
    ///     Alpha is dropped (track it separately). For greys (chroma 0) hue is reported as 0.
    /// </summary>
    public static (float h, float s, float v) ToHsv(Color c)
    {
        float r = Math.Clamp(value: c.R, min: 0f, max: 1f);
        float g = Math.Clamp(value: c.G, min: 0f, max: 1f);
        float b = Math.Clamp(value: c.B, min: 0f, max: 1f);

        float max = MathF.Max(x: r, y: MathF.Max(x: g, y: b));
        float min = MathF.Min(x: r, y: MathF.Min(x: g, y: b));
        float delta = max - min;

        float v = max;
        float s = max <= 0f ? 0f : delta / max;

        float h;
        if (delta <= 1e-6f)
            h = 0f;
        else if (max == r)
            h = 60f * ((g - b) / delta % 6f);
        else if (max == g)
            h = 60f * (((b - r) / delta) + 2f);
        else
            h = 60f * (((r - g) / delta) + 4f);

        if (h < 0f) h += 360f;
        return (h, s, v);
    }

    /// <summary>
    ///     Convert HSV (h in degrees, s/v in [0,1]) plus an alpha to an RGBA <see cref="Color" />.
    ///     Uses the public <c>Color(float,float,float,float)</c> constructor.
    /// </summary>
    public static Color FromHsv(float h, float s, float v, float a = 1f)
    {
        h = WrapHue(h);
        s = Math.Clamp(value: s, min: 0f, max: 1f);
        v = Math.Clamp(value: v, min: 0f, max: 1f);

        float c = v * s;
        float x = c * (1f - MathF.Abs((h / 60f % 2f) - 1f));
        float m = v - c;

        float r, g, b;
        if (h < 60f) (r, g, b) = (c, x, 0f);
        else if (h < 120f) (r, g, b) = (x, c, 0f);
        else if (h < 180f) (r, g, b) = (0f, c, x);
        else if (h < 240f) (r, g, b) = (0f, x, c);
        else if (h < 300f) (r, g, b) = (x, 0f, c);
        else (r, g, b) = (c, 0f, x);

        return new Color(
            r: r + m,
            g: g + m,
            b: b + m,
            a: Math.Clamp(value: a, min: 0f, max: 1f)
        );
    }

    /// <summary>
    ///     Format a colour as an uppercase hex string with a leading <c>#</c>. Without
    ///     <paramref name="includeAlpha" /> the result is <c>#RRGGBB</c>; with it, <c>#RRGGBBAA</c>.
    /// </summary>
    public static string ToHex(Color c, bool includeAlpha = false)
    {
        byte r = ToByte(c.R);
        byte g = ToByte(c.G);
        byte b = ToByte(c.B);
        if (!includeAlpha) return $"#{r:X2}{g:X2}{b:X2}";
        byte a = ToByte(c.A);
        return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
    }

    /// <summary>
    ///     Parse <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (with or without the leading <c>#</c>).
    ///     Whitespace is trimmed. Returns <c>false</c> on any malformed input.
    /// </summary>
    public static bool TryParseHex(string s, out Color c)
    {
        c = Color.Black;
        if (string.IsNullOrWhiteSpace(s)) return false;

        string t = s.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 0) return false;

        // Validate hex digits.
        foreach (char ch in t)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }

        switch (t.Length)
        {
            case 3: // RGB → expand each nibble (e.g. "abc" → "aabbcc")
            {
                int r = ParseNibble(t[0]);
                int g = ParseNibble(t[1]);
                int b = ParseNibble(t[2]);
                c = new Color(r: r / 255f, g: g / 255f, b: b / 255f);
                return true;
            }
            case 6: // RRGGBB
            {
                int r = ParseByte(s: t, index: 0);
                int g = ParseByte(s: t, index: 2);
                int b = ParseByte(s: t, index: 4);
                c = new Color(r: r / 255f, g: g / 255f, b: b / 255f);
                return true;
            }
            case 8: // RRGGBBAA
            {
                int r = ParseByte(s: t, index: 0);
                int g = ParseByte(s: t, index: 2);
                int b = ParseByte(s: t, index: 4);
                int a = ParseByte(s: t, index: 6);
                c = new Color(
                    r: r / 255f,
                    g: g / 255f,
                    b: b / 255f,
                    a: a / 255f
                );
                return true;
            }
            default:
                return false;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static float WrapHue(float h)
    {
        h %= 360f;
        if (h < 0f) h += 360f;
        return h;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(
        value: (int)MathF.Round(v * 255f),
        min: 0,
        max: 255
    );

    private static int ParseNibble(char c)
    {
        int n = Convert.ToInt32(value: c.ToString(), fromBase: 16);
        return (n * 16) + n; // duplicate nibble so "a" → 0xAA
    }

    private static int ParseByte(string s, int index) => int.Parse(
        s: s.AsSpan(start: index, length: 2),
        style: NumberStyles.HexNumber,
        provider: CultureInfo.InvariantCulture
    );
}
