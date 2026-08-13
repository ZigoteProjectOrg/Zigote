using Zigote.Core.Math3D;

namespace Zigote.Core;

/// <summary>
///     RGBA color with f32 components in [0, 1].
///     Matches the Zig Color struct conventions (Material palette names).
/// </summary>
public readonly struct Color(float r, float g, float b, float a = 1f) : IEquatable<Color>
{
    public readonly float R = r;
    public readonly float G = g;
    public readonly float B = b;
    public readonly float A = a;

    /// <summary>
    ///     Packed 0xAARRGGBB constructor, e.g. <c>new Color(0xFF2196F3)</c>, so hex colour literals
    ///     compile unchanged. The <c>uint</c>
    ///     parameter never collides with the <c>(float, float, float, float)</c> component ctor: a
    ///     hex literal binds here, three floats bind there.
    /// </summary>
    public Color(uint argb) : this(
        r: ((argb >> 16) & 0xFF) / 255f,
        g: ((argb >> 8) & 0xFF) / 255f,
        b: (argb & 0xFF) / 255f,
        a: ((argb >> 24) & 0xFF) / 255f
    ) { }

    private static Color FromBytes(byte r, byte g, byte b, byte a = 255)
    {
        return new Color(
            r: r / 255f,
            g: g / 255f,
            b: b / 255f,
            a: a / 255f
        );
    }

    public static Color FromHex(uint argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return FromBytes(
            r: r,
            g: g,
            b: b,
            a: a
        );
    }

    /// <summary>Opaque colour from 0–255 integer channels — e.g. <c>Color.Rgb(16, 18, 22)</c>.</summary>
    public static Color Rgb(int r, int g, int b) => new(r: r / 255f, g: g / 255f, b: b / 255f);

    /// <summary>Colour from 0–255 integer channels with a 0–1 <paramref name="a" /> alpha.</summary>
    public static Color Rgba(int r, int g, int b, float a)
    {
        return new Color(
            r: r / 255f,
            g: g / 255f,
            b: b / 255f,
            a: a
        );
    }

    public Color WithAlpha(float alpha)
    {
        return new Color(
            r: R,
            g: G,
            b: B,
            a: alpha
        );
    }

    /// <summary>Darken toward black by <paramref name="t" /> (0..1), preserving alpha.</summary>
    public Color Darken(float t)
    {
        float k = Math.Clamp(value: 1f - t, min: 0f, max: 1f);
        return new Color(
            r: R * k,
            g: G * k,
            b: B * k,
            a: A
        );
    }

    /// <summary>Lighten toward white by <paramref name="t" /> (0..1), preserving alpha.</summary>
    public Color Lighten(float t)
    {
        t = Math.Clamp(value: t, min: 0f, max: 1f);
        return new Color(
            r: R + ((1f - R) * t),
            g: G + ((1f - G) * t),
            b: B + ((1f - B) * t),
            a: A
        );
    }

    // Material palette — most used subset
    public static readonly Color Transparent = new(
        r: 0,
        g: 0,
        b: 0,
        a: 0
    );

    public static readonly Color Black = FromHex(0xFF000000);
    public static readonly Color White = FromHex(0xFFFFFFFF);

    public static readonly Color Red = FromHex(0xFFF44336);
    public static readonly Color Pink = FromHex(0xFFE91E63);
    public static readonly Color Purple = FromHex(0xFF9C27B0);
    public static readonly Color Indigo = FromHex(0xFF3F51B5);
    public static readonly Color Blue = FromHex(0xFF2196F3);
    public static readonly Color Cyan = FromHex(0xFF00BCD4);
    public static readonly Color Teal = FromHex(0xFF009688);
    public static readonly Color Green = FromHex(0xFF4CAF50);
    public static readonly Color Lime = FromHex(0xFFCDDC39);
    public static readonly Color Yellow = FromHex(0xFFFFEB3B);
    public static readonly Color Amber = FromHex(0xFFFFC107);
    public static readonly Color Orange = FromHex(0xFFFF9800);
    public static readonly Color DeepOrange = FromHex(0xFFFF5722);
    public static readonly Color Brown = FromHex(0xFF795548);
    public static readonly Color Grey = FromHex(0xFF9E9E9E);
    public static readonly Color BlueGrey = FromHex(0xFF607D8B);

    // Exact value equality — consistent with GetHashCode, so Color is safe as a dictionary/set key.
    // For tolerant comparison use ApproxEquals.
    public bool Equals(Color other) => R.Equals(other.R) && G.Equals(other.G) &&
                                       B.Equals(other.B) && A.Equals(other.A);

    /// <summary>Tolerant component-wise comparison.</summary>
    public bool ApproxEquals(Color other, float tolerance = Tolerance.StandardValue)
    {
        return Math.Abs(R - other.R) < tolerance &&
               Math.Abs(G - other.G) < tolerance &&
               Math.Abs(B - other.B) < tolerance &&
               Math.Abs(A - other.A) < tolerance;
    }

    public override bool Equals(object? obj) => obj is Color c && Equals(c);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            value1: R,
            value2: G,
            value3: B,
            value4: A
        );
    }

    public static bool operator ==(Color a, Color b) => a.Equals(b);

    public static bool operator !=(Color a, Color b) => !a.Equals(b);

    public override string ToString() => $"Color({R:F2}, {G:F2}, {B:F2}, {A:F2})";
}
