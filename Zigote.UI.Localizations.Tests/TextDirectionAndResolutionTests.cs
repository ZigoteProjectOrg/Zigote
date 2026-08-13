namespace Zigote.UI.Localizations.Tests;

public class TextDirectionTests
{
    [Theory]
    [InlineData("en", TextDirection.Ltr)]
    [InlineData("ru", TextDirection.Ltr)]
    [InlineData("zh", TextDirection.Ltr)]
    [InlineData("ar", TextDirection.Rtl)]
    [InlineData("he", TextDirection.Rtl)]
    [InlineData("fa", TextDirection.Rtl)]
    [InlineData("ur", TextDirection.Rtl)]
    public void Language_direction(string tag, TextDirection expected)
    {
        Assert.Equal(expected, Locale.Parse(tag).TextDirection);
    }

    [Fact]
    public void Script_overrides_language_direction()
    {
        // Azerbaijani is LTR in Latin but RTL in the Arabic script.
        Assert.Equal(TextDirection.Rtl, Locale.Parse("az-Arab").TextDirection);
        Assert.Equal(TextDirection.Ltr, Locale.Parse("az-Latn").TextDirection);
        // Hebrew written in Latin transliteration is LTR.
        Assert.Equal(TextDirection.Ltr, Locale.Parse("he-Latn").TextDirection);
    }
}

public class LocaleResolutionTests
{
    private static readonly List<Locale> Supported = [
        Locale.Parse("en"), Locale.Parse("en-GB"), Locale.Parse("es"), Locale.Parse("es-MX"),
        Locale.Parse("zh-Hans"), Locale.Parse("zh-Hant"), Locale.Parse("fr"),
    ];

    [Fact]
    public void Exact_match_wins()
    {
        Assert.Equal(
            Locale.Parse("es-MX"),
            LocaleResolution.Resolve(Locale.Parse("es-MX"), Supported, Locale.Parse("en"))
        );
    }

    [Fact]
    public void Falls_back_to_language_only()
    {
        // fr-CA not supported -> the bare fr entry.
        Assert.Equal(
            Locale.Parse("fr"),
            LocaleResolution.Resolve(Locale.Parse("fr-CA"), Supported, Locale.Parse("en"))
        );
    }

    [Fact]
    public void Falls_back_to_same_language_region_variant()
    {
        // es-AR not present, but es and es-MX are; language-only es wins over region variant.
        Assert.Equal(
            Locale.Parse("es"),
            LocaleResolution.Resolve(Locale.Parse("es-AR"), Supported, Locale.Parse("en"))
        );
    }

    [Fact]
    public void Matches_script_when_present()
    {
        Assert.Equal(
            Locale.Parse("zh-Hant"),
            LocaleResolution.Resolve(Locale.Parse("zh-Hant-HK"), Supported, Locale.Parse("en"))
        );
    }

    [Fact]
    public void Preference_order_beats_match_tightness()
    {
        // First preference (de, unsupported) loses; second (es-MX exact) wins over later prefs.
        var preferred = new[] {
            Locale.Parse("de"),
            Locale.Parse("es-MX"),
            Locale.Parse("en"),
        };
        Assert.Equal(
            Locale.Parse("es-MX"),
            LocaleResolution.Resolve(preferred, Supported, Locale.Parse("en"))
        );
    }

    [Fact]
    public void No_match_returns_fallback_when_supported()
    {
        Assert.Equal(
            Locale.Parse("en"),
            LocaleResolution.Resolve(Locale.Parse("ja"), Supported, Locale.Parse("en"))
        );
    }

    [Fact]
    public void No_match_and_fallback_absent_returns_first_supported()
    {
        Assert.Equal(
            Supported[0],
            LocaleResolution.Resolve(Locale.Parse("ja"), Supported, Locale.Parse("ko"))
        );
    }

    [Fact]
    public void Empty_supported_returns_fallback_or_preference()
    {
        Assert.Equal(
            Locale.Parse("de"),
            LocaleResolution.Resolve(Locale.Parse("de"), [], Locale.Parse("de"))
        );
        Assert.Equal(
            Locale.Parse("de"),
            LocaleResolution.Resolve(Locale.Parse("de"), [], default)
        );
    }
}
