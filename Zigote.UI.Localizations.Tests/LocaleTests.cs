namespace Zigote.UI.Localizations.Tests;

public class LocaleTests
{
    [Theory]
    [InlineData(
        "en",
        "en",
        null,
        null
    )]
    [InlineData(
        "en-US",
        "en",
        null,
        "US"
    )]
    [InlineData(
        "en_US",
        "en",
        null,
        "US"
    )]
    [InlineData(
        "EN-us",
        "en",
        null,
        "US"
    )]
    [InlineData(
        "zh-Hant",
        "zh",
        "Hant",
        null
    )]
    [InlineData(
        "zh-hant-tw",
        "zh",
        "Hant",
        "TW"
    )]
    [InlineData(
        "zh_Hant_TW",
        "zh",
        "Hant",
        "TW"
    )]
    [InlineData(
        "es-419",
        "es",
        null,
        "419"
    )]
    [InlineData(
        "sr-Latn-RS",
        "sr",
        "Latn",
        "RS"
    )]
    [InlineData(
        "de-DE-1996",
        "de",
        null,
        "DE"
    )] // variant subtag ignored
    [InlineData(
        "en.UTF-8",
        "en",
        null,
        null
    )] // POSIX codeset stripped
    [InlineData(
        "en_US@euro",
        "en",
        null,
        "US"
    )] // POSIX variant stripped
    public void Parse_normalizes_subtags(string tag, string lang, string? script, string? country)
    {
        var l = Locale.Parse(tag);
        Assert.Equal(expected: lang, actual: l.Language);
        Assert.Equal(expected: script, actual: l.Script);
        Assert.Equal(expected: country, actual: l.Country);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")] // language must be alpha
    [InlineData("e")] // too short
    [InlineData("englishlanguage")] // too long for a primary subtag
    public void TryParse_rejects_garbage(string tag) =>
        Assert.False(Locale.TryParse(tag: tag, locale: out _));

    [Fact]
    public void Parse_throws_on_invalid() =>
        Assert.Throws<FormatException>(() => Locale.Parse("!!"));

    [Theory]
    [InlineData("en", "en")]
    [InlineData("zh-Hant-TW", "zh-Hant-TW")]
    [InlineData("en_US", "en-US")]
    public void ToBcp47_is_canonical_hyphenated(string tag, string expected) => Assert.Equal(
        expected: expected,
        actual: Locale.Parse(tag).ToBcp47()
    );

    [Fact]
    public void ToUnderscore_uses_underscores() => Assert.Equal(
        expected: "zh_Hant_TW",
        actual: Locale.Parse("zh-Hant-TW").ToUnderscore()
    );

    [Fact]
    public void Equality_is_case_insensitive_and_hash_consistent()
    {
        var a = Locale.Parse("EN-us");
        var b = Locale.Parse("en-US");
        Assert.Equal(expected: a, actual: b);
        Assert.True(a == b);
        Assert.Equal(expected: a.GetHashCode(), actual: b.GetHashCode());
    }

    [Fact]
    public void Different_locales_are_unequal()
    {
        Assert.NotEqual(expected: Locale.Parse("en-US"), actual: Locale.Parse("en-GB"));
        Assert.NotEqual(expected: Locale.Parse("zh-Hans"), actual: Locale.Parse("zh-Hant"));
        Assert.True(Locale.Parse("en") != Locale.Parse("es"));
    }

    [Fact]
    public void Implicit_string_conversion_parses()
    {
        Locale l = "fr-FR";
        Assert.Equal(expected: "fr", actual: l.Language);
        Assert.Equal(expected: "FR", actual: l.Country);
    }

    [Fact]
    public void Default_struct_is_empty()
    {
        Locale l = default;
        Assert.True(l.IsEmpty);
        Assert.Equal(expected: "(none)", actual: l.ToString());
    }

    [Fact]
    public void LanguageOnly_and_WithoutScript_reduce()
    {
        var l = Locale.Parse("zh-Hant-TW");
        Assert.Equal(expected: Locale.Parse("zh"), actual: l.LanguageOnly());
        Assert.Equal(expected: Locale.Parse("zh-TW"), actual: l.WithoutScript());
    }

    [Fact]
    public void Locales_usable_as_dictionary_keys()
    {
        var d = new Dictionary<Locale, int> { [Locale.Parse("en-US")] = 1 };
        Assert.Equal(expected: 1, actual: d[Locale.Parse("en_US")]);
    }
}
