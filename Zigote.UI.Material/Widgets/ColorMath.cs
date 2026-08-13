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
        var r = Math.Clamp(c.R, 0f, 1f);
        var g = Math.Clamp(c.G, 0f, 1f);
        var b = Math.Clamp(c.B, 0f, 1f);

        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var delta = max - min;

        var v = max;
        var s = max <= 0f ? 0f : delta / max;

        float h;
        if (delta <= 1e-6f)
            h = 0f;
        else if (max == r)
            h = 60f * ((g - b) / delta % 6f);
        else if (max == g)
            h = 60f * ((b - r) / delta + 2f);
        else
            h = 60f * ((r - g) / delta + 4f);

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
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);

        var c = v * s;
        var x = c * (1f - MathF.Abs(h / 60f % 2f - 1f));
        var m = v - c;

        float r, g, b;
        if (h < 60f) (r, g, b) = (c, x, 0f);
        else if (h < 120f) (r, g, b) = (x, c, 0f);
        else if (h < 180f) (r, g, b) = (0f, c, x);
        else if (h < 240f) (r, g, b) = (0f, x, c);
        else if (h < 300f) (r, g, b) = (x, 0f, c);
        else (r, g, b) = (c, 0f, x);

        return new Color(
            r + m,
            g + m,
            b + m,
            Math.Clamp(a, 0f, 1f)
        );
    }

    /// <summary>
    ///     Format a colour as an uppercase hex string with a leading <c>#</c>. Without
    ///     <paramref name="includeAlpha" /> the result is <c>#RRGGBB</c>; with it, <c>#RRGGBBAA</c>.
    /// </summary>
    public static string ToHex(Color c, bool includeAlpha = false)
    {
        var r = ToByte(c.R);
        var g = ToByte(c.G);
        var b = ToByte(c.B);
        if (!includeAlpha) return $"#{r:X2}{g:X2}{b:X2}";
        var a = ToByte(c.A);
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

        var t = s.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 0) return false;

        // Validate hex digits.
        foreach (var ch in t)
            if (!Uri.IsHexDigit(ch))
                return false;

        switch (t.Length)
        {
            case 3: // RGB → expand each nibble (e.g. "abc" → "aabbcc")
            {
                var r = ParseNibble(t[0]);
                var g = ParseNibble(t[1]);
                var b = ParseNibble(t[2]);
                c = new Color(r / 255f, g / 255f, b / 255f);
                return true;
            }
            case 6: // RRGGBB
            {
                var r = ParseByte(t, 0);
                var g = ParseByte(t, 2);
                var b = ParseByte(t, 4);
                c = new Color(r / 255f, g / 255f, b / 255f);
                return true;
            }
            case 8: // RRGGBBAA
            {
                var r = ParseByte(t, 0);
                var g = ParseByte(t, 2);
                var b = ParseByte(t, 4);
                var a = ParseByte(t, 6);
                c = new Color(
                    r / 255f,
                    g / 255f,
                    b / 255f,
                    a / 255f
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

    private static byte ToByte(float v)
    {
        return (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
    }

    private static int ParseNibble(char c)
    {
        var n = Convert.ToInt32(c.ToString(), 16);
        return n * 16 + n; // duplicate nibble so "a" → 0xAA
    }

    private static int ParseByte(string s, int index)
    {
        return int.Parse(s.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
