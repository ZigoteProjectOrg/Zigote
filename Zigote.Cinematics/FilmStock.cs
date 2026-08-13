namespace Zigote.Cinematics;

/// <summary>Named film-stock emulations. Each maps to a preset of the renderer's grade knobs.</summary>
public enum FilmStockKind
{
    Neutral,
    Kodak2383,
    KodakVision3,
    FujiEterna,
    Ektachrome,
    CineonLog,
    Bw,
}

/// <summary>
///     A film-stock look expressed as the renderer's existing tonemap grade knobs (AgX look, white
///     balance, saturation, contrast, grain, vignette). <see cref="Of" /> resolves a
///     <see cref="Kind" />
///     at a given <see cref="Strength" /> by blending from the <see cref="Neutral" /> baseline toward
///     the
///     stock's target values, so Strength 0 == Neutral and Strength 1 == the full stock.
///     <see cref="LutId" /> selects a native 3D-LUT (native phase); 0 = none.
/// </summary>
public readonly struct FilmStock : IEquatable<FilmStock>
{
    public FilmStockKind Kind { get; init; }
    public float Strength { get; init; }

    /// <summary>AgX post-look: 0 = Default (neutral AgX), 1 = Punchy, 2 = Golden.</summary>
    public int Look { get; init; }

    public float Contrast { get; init; }
    public float Saturation { get; init; }
    public float WbTemperature { get; init; }
    public float WbTint { get; init; }
    public float Grain { get; init; }
    public float Vignette { get; init; }
    public int LutId { get; init; }

    /// <summary>The engine's default photographic baseline (matches the Zig <c>Settings3D</c> defaults).</summary>
    public static FilmStock Neutral => new() {
        Kind = FilmStockKind.Neutral,
        Strength = 1f,
        Look = 1,
        Contrast = 0.34f,
        Saturation = 1.20f,
        WbTemperature = 0.10f,
        WbTint = 0f,
        Grain = 0.015f,
        Vignette = 0.18f,
        LutId = 0,
    };

    /// <summary>
    ///     Resolve a stock at <paramref name="strength" /> (0..1), blended from
    ///     <see cref="Neutral" />.
    /// </summary>
    public static FilmStock Of(FilmStockKind kind, float strength)
    {
        float t = Math.Clamp(value: strength, min: 0f, max: 1f);
        var target = Target(kind);
        var n = Neutral;
        return new FilmStock {
            Kind = kind,
            Strength = t,
            Look = (int)MathF.Round(Lerp(a: n.Look, b: target.Look, t: t)),
            Contrast = Lerp(a: n.Contrast, b: target.Contrast, t: t),
            Saturation = Lerp(a: n.Saturation, b: target.Saturation, t: t),
            WbTemperature = Lerp(a: n.WbTemperature, b: target.WbTemperature, t: t),
            WbTint = Lerp(a: n.WbTint, b: target.WbTint, t: t),
            Grain = Lerp(a: n.Grain, b: target.Grain, t: t),
            Vignette = Lerp(a: n.Vignette, b: target.Vignette, t: t),
            LutId = target.LutId,
        };
    }

    // Absolute target knob values for each stock at full strength.
    private static FilmStock Target(FilmStockKind kind)
    {
        return kind switch {
            // Warm cinematic print emulation.
            FilmStockKind.Kodak2383 => new FilmStock {
                Look = 2,
                Contrast = 0.42f,
                Saturation = 1.15f,
                WbTemperature = 0.18f,
                WbTint = 0.02f,
                Grain = 0.030f,
                Vignette = 0.25f,
                LutId = 1,
            },
            // Modern natural negative.
            FilmStockKind.KodakVision3 => new FilmStock {
                Look = 0,
                Contrast = 0.30f,
                Saturation = 1.10f,
                WbTemperature = 0.06f,
                WbTint = 0f,
                Grain = 0.020f,
                Vignette = 0.12f,
                LutId = 2,
            },
            // Low-contrast, cool, muted cine stock.
            FilmStockKind.FujiEterna => new FilmStock {
                Look = 0,
                Contrast = 0.20f,
                Saturation = 0.95f,
                WbTemperature = -0.05f,
                WbTint = -0.02f,
                Grain = 0.020f,
                Vignette = 0.10f,
                LutId = 3,
            },
            // Punchy saturated slide film.
            FilmStockKind.Ektachrome => new FilmStock {
                Look = 1,
                Contrast = 0.45f,
                Saturation = 1.35f,
                WbTemperature = -0.03f,
                WbTint = 0f,
                Grain = 0.015f,
                Vignette = 0.15f,
                LutId = 4,
            },
            // Flat log for grading downstream.
            FilmStockKind.CineonLog => new FilmStock {
                Look = 0,
                Contrast = 0.05f,
                Saturation = 0.85f,
                WbTemperature = 0f,
                WbTint = 0f,
                Grain = 0f,
                Vignette = 0f,
                LutId = 5,
            },
            // Black & white.
            FilmStockKind.Bw => new FilmStock {
                Look = 1,
                Contrast = 0.50f,
                Saturation = 0f,
                WbTemperature = 0f,
                WbTint = 0f,
                Grain = 0.040f,
                Vignette = 0.22f,
                LutId = 6,
            },
            _ => Neutral,
        };
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    public bool Equals(FilmStock other)
    {
        return Kind == other.Kind && Strength.Equals(other.Strength) && Look == other.Look &&
               Contrast.Equals(other.Contrast) && Saturation.Equals(other.Saturation) &&
               WbTemperature.Equals(other.WbTemperature) && WbTint.Equals(other.WbTint) &&
               Grain.Equals(other.Grain) && Vignette.Equals(other.Vignette) && LutId == other.LutId;
    }

    public override bool Equals(object? obj) => obj is FilmStock o && Equals(o);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            value1: Kind,
            value2: Strength,
            value3: Look,
            value4: Contrast,
            value5: Saturation,
            value6: WbTemperature,
            value7: HashCode.Combine(
                value1: WbTint,
                value2: Grain,
                value3: Vignette,
                value4: LutId
            )
        );
    }
}
