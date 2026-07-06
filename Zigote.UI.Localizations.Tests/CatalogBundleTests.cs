namespace Zigote.UI.Localizations.Tests;

public class LocalizationCatalogTests
{
    private static LocalizationCatalog En()
    {
        return new LocalizationCatalog(Locale.En) {
            ["greeting"] = "Hello, {name}!",
            ["items"] = "{count, plural, one {# item} other {# items}}",
        };
    }

    [Fact]
    public void Translate_with_placeholder()
    {
        Assert.Equal("Hello, Amir!", En().Translate("greeting", ("name", "Amir")));
    }

    [Theory]
    [InlineData(1, "1 item")]
    [InlineData(4, "4 items")]
    public void Translate_with_plural(int count, string expected)
    {
        Assert.Equal(expected, En().Translate("items", ("count", count)));
    }

    [Fact]
    public void Missing_key_returns_null()
    {
        Assert.Null(En().Translate("nope"));
    }

    [Fact]
    public void Indexer_set_replaces_and_invalidates_compiled_form()
    {
        var c = En();
        Assert.Equal("Hello, Amir!", c.Translate("greeting", ("name", "Amir")));
        c["greeting"] = "Hi {name}";
        Assert.Equal("Hi Amir", c.Translate("greeting", ("name", "Amir")));
    }

    [Fact]
    public void Malformed_template_falls_back_to_raw_text()
    {
        var c = new LocalizationCatalog(Locale.En) { ["bad"] = "{oops" };
        Assert.Equal("{oops", c.Translate("bad"));
    }

    [Fact]
    public void Enumerates_and_counts()
    {
        var c = En();
        Assert.Equal(2, c.Count);
        Assert.Contains("greeting", c.Keys);
#pragma warning disable CA1829 // deliberately exercises IEnumerable enumeration, not the property
        Assert.Equal(2, c.Count());
#pragma warning restore CA1829
    }
}

public class LocalizationBundleTests
{
    private static LocalizationBundle Build()
    {
        var en = new LocalizationCatalog(Locale.En) {
            ["greeting"] = "Hello",
            ["only_en"] = "English only",
        };
        var es = new LocalizationCatalog(Locale.Es) { ["greeting"] = "Hola" };
        return new LocalizationBundle(en, es);
    }

    [Fact]
    public void First_catalog_becomes_default_fallback()
    {
        Assert.Equal(Locale.En, Build().FallbackLocale);
    }

    [Fact]
    public void Translate_uses_requested_locale()
    {
        Assert.Equal("Hola", Build().Translate(Locale.Es, "greeting"));
    }

    [Fact]
    public void Missing_key_falls_back_to_fallback_locale()
    {
        Assert.Equal("English only", Build().Translate(Locale.Es, "only_en"));
    }

    [Fact]
    public void Same_language_region_variant_resolves()
    {
        Assert.Equal("Hola", Build().Translate(Locale.Parse("es-MX"), "greeting"));
    }

    [Fact]
    public void Total_miss_returns_key_by_default()
    {
        Assert.Equal("absent", Build().Translate(Locale.Es, "absent"));
    }

    [Fact]
    public void Total_miss_empty_policy()
    {
        var b = Build();
        b.MissingPolicy = MissingTranslationPolicy.Empty;
        Assert.Equal("", b.Translate(Locale.Es, "absent"));
    }

    [Fact]
    public void Total_miss_throw_policy()
    {
        var b = Build();
        b.MissingPolicy = MissingTranslationPolicy.Throw;
        Assert.Throws<KeyNotFoundException>(() => b.Translate(Locale.Es, "absent"));
    }

    [Fact]
    public void OnMissing_handler_wins_over_policy()
    {
        var b = Build();
        b.OnMissing = (key, _) => $"<{key}>";
        Assert.Equal("<absent>", b.Translate(Locale.Es, "absent"));
    }

    [Fact]
    public void Supports_reports_language_coverage()
    {
        var b = Build();
        Assert.True(b.Supports(Locale.En));
        Assert.True(b.Supports(Locale.Parse("es-AR")));
        Assert.False(b.Supports(Locale.Fr));
    }

    [Fact]
    public void StringLocalizations_payload_translates_and_contains()
    {
        var strings = Build().For(Locale.Es);
        Assert.Equal("Hola", strings["greeting"]);
        Assert.True(strings.Contains("only_en")); // via fallback
        Assert.False(strings.Contains("absent"));
    }
}