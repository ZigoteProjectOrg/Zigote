namespace Zigote.UI.Localizations.Tests;

public class LocalizationJsonTests
{
    [Fact]
    public void LoadCatalog_reads_strings_and_skips_arb_metadata()
    {
        const string json = """
                            {
                                "@@locale": "en",
                                "greeting": "Hello, {name}!",
                                "@greeting": { "description": "a greeting" },
                                "count": 3
                            }
                            """;
        var catalog = LocalizationJson.LoadCatalog(json, Locale.En);
        Assert.Equal("Hello, Alex!", catalog.Translate("greeting", ("name", "Alex")));
        Assert.False(catalog.Contains("@greeting")); // metadata skipped
        Assert.False(catalog.Contains("count")); // non-string skipped
    }

    [Fact]
    public void LoadBundle_reads_multiple_locales_and_skips_unparseable_keys()
    {
        const string json = """
                            {
                                "en": { "hi": "Hello" },
                                "es": { "hi": "Hola" },
                                "not a locale!": { "hi": "ignored" },
                                "meta": "also ignored (not an object)"
                            }
                            """;
        var bundle = LocalizationJson.LoadBundle(json);
        Assert.Equal("Hello", bundle.Translate(Locale.En, "hi"));
        Assert.Equal("Hola", bundle.Translate(Locale.Es, "hi"));
        Assert.Equal(2, bundle.Locales.Count);
    }

    [Fact]
    public void Non_object_root_throws()
    {
        Assert.Throws<FormatException>(() => LocalizationJson.LoadCatalog("[]", Locale.En));
    }
}

public class LocaleFormattingTests
{
    [Fact]
    public void Unknown_locale_falls_back_without_throwing()
    {
        var f = LocaleFormatting.For(Locale.Parse("xyz"));
        Assert.NotNull(f.Number(1234.5));
    }

    [Fact]
    public void Number_and_integer_use_grouping()
    {
        var f = LocaleFormatting.For(Locale.EnUs);
        Assert.Equal("1,000", f.Integer(1000));
        Assert.Contains("234", f.Number(1234.5));
    }

    [Fact]
    public void Percent_multiplies_by_hundred()
    {
        Assert.Contains("50", LocaleFormatting.For(Locale.EnUs).Percent(0.5));
    }

    [Fact]
    public void Currency_with_explicit_code_uses_symbol()
    {
        var s = LocaleFormatting.For(Locale.EnUs).Currency(1234.56m, "USD");
        Assert.Contains("$", s);
    }

    [Theory]
    [InlineData("ja")] // "yyyy'年'M'月'd'日'"
    [InlineData("ru")] // "d MMMM yyyy 'г.'"
    [InlineData("ar")]
    [InlineData("de")]
    [InlineData("en-US")]
    public void Date_formatting_never_throws_across_cultures(string tag)
    {
        var f = LocaleFormatting.For(Locale.Parse(tag));
        var d = new DateTime(
            2026,
            10,
            6,
            16,
            7,
            12
        );
        Assert.False(string.IsNullOrWhiteSpace(f.Date(d, DateStyle.Short)));
        Assert.False(string.IsNullOrWhiteSpace(f.Date(d)));
        Assert.False(string.IsNullOrWhiteSpace(f.Date(d, DateStyle.Long)));
        Assert.False(string.IsNullOrWhiteSpace(f.Time(d)));
        Assert.False(string.IsNullOrWhiteSpace(f.DateTime(d)));
    }

    [Fact]
    public void Formatter_instances_are_cached_per_locale()
    {
        Assert.Same(LocaleFormatting.For(Locale.EnUs), LocaleFormatting.For(Locale.EnUs));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1234.5)]
    public void Number_handles_edge_values(double v)
    {
        Assert.NotNull(LocaleFormatting.For(Locale.EnUs).Number(v));
    }
}