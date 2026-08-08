namespace Zigote.Core.Paint;

/// <summary>
///     Maps a requested <see cref="FontWeight" /> onto the named face registered for it. The
///     FreeType renderer shapes one face per family and ignores the weight field, so hosts
///     register each bundled weight file as its own family at startup (e.g. "Inter-Bold") and
///     both text emission (<c>PaintList.AddText</c>) and measurement
///     (<c>ZigoteEngine.MeasureText</c>) resolve through here — draw and measure can never
///     disagree. Explicit families ("code", "MaterialIcons") pass through untouched.
/// </summary>
public static class FontFaces
{
    // Index = weight / 100 (1..9). null = no dedicated face; falls back to the nearest
    // registered face at or below the requested weight, then to the default family.
    private static readonly string?[] Buckets = new string?[10];

    /// <summary>Register the family name loaded for <paramref name="weight" /> (default-face variants only).</summary>
    public static void RegisterWeight(FontWeight weight, string family)
    {
        Buckets[(int)weight / 100] = family;
    }

    /// <summary>
    ///     The family to shape <paramref name="requested" /> with at <paramref name="weight" />.
    ///     Non-null requests (icon/monospace faces) win; the default face resolves to the nearest
    ///     registered weight variant at or below, or stays null (engine default face).
    /// </summary>
    public static string? Resolve(FontWeight weight, string? requested)
    {
        if (!string.IsNullOrEmpty(requested)) return requested;
        for (var i = (int)weight / 100; i >= 1; i--)
            if (Buckets[i] is { } family)
                return family;
        return null;
    }
}