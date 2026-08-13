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
    public void Translate_with_placeholder() => Assert.Equal(
        expected: "Hello, Alex!",
        actual: En().Translate(key: "greeting", ("name", "Alex"))
    );

    [Theory]
    [InlineData(1, "1 item")]
    [InlineData(4, "4 items")]
    public void Translate_with_plural(int count, string expected) => Assert.Equal(
        expected: expected,
        actual: En().Translate(key: "items", ("count", count))
    );

    [Fact]
    public void Missing_key_returns_null() => Assert.Null(En().Translate("nope"));

    [Fact]
    public void Indexer_set_replaces_and_invalidates_compiled_form()
    {
        var c = En();
        Assert.Equal(
            expected: "Hello, Alex!",
            actual: c.Translate(key: "greeting", ("name", "Alex"))
        );
        c["greeting"] = "Hi {name}";
        Assert.Equal(expected: "Hi Alex", actual: c.Translate(key: "greeting", ("name", "Alex")));
    }

    [Fact]
    public void Malformed_template_falls_back_to_raw_text()
    {
        var c = new LocalizationCatalog(Locale.En) { ["bad"] = "{oops" };
        Assert.Equal(expected: "{oops", actual: c.Translate("bad"));
    }

    [Fact]
    public void Enumerates_and_counts()
    {
        var c = En();
        Assert.Equal(expected: 2, actual: c.Count);
        Assert.Contains(expected: "greeting", collection: c.Keys);
#pragma warning disable CA1829 // deliberately exercises IEnumerable enumeration, not the property
        Assert.Equal(expected: 2, actual: c.Count());
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
    public void First_catalog_becomes_default_fallback() => Assert.Equal(
        expected: Locale.En,
        actual: Build().FallbackLocale
    );

    [Fact]
    public void Translate_uses_requested_locale() => Assert.Equal(
        expected: "Hola",
        actual: Build().Translate(locale: Locale.Es, key: "greeting")
    );

    [Fact]
    public void Missing_key_falls_back_to_fallback_locale() => Assert.Equal(
        expected: "English only",
        actual: Build().Translate(locale: Locale.Es, key: "only_en")
    );

    [Fact]
    public void Same_language_region_variant_resolves() => Assert.Equal(
        expected: "Hola",
        actual: Build().Translate(locale: Locale.Parse("es-MX"), key: "greeting")
    );

    [Fact]
    public void Total_miss_returns_key_by_default() => Assert.Equal(
        expected: "absent",
        actual: Build().Translate(locale: Locale.Es, key: "absent")
    );

    [Fact]
    public void Total_miss_empty_policy()
    {
        var b = Build();
        b.MissingPolicy = MissingTranslationPolicy.Empty;
        Assert.Equal(expected: "", actual: b.Translate(locale: Locale.Es, key: "absent"));
    }

    [Fact]
    public void Total_miss_throw_policy()
    {
        var b = Build();
        b.MissingPolicy = MissingTranslationPolicy.Throw;
        Assert.Throws<KeyNotFoundException>(() => b.Translate(locale: Locale.Es, key: "absent"));
    }

    [Fact]
    public void OnMissing_handler_wins_over_policy()
    {
        var b = Build();
        b.OnMissing = (key, _) => $"<{key}>";
        Assert.Equal(expected: "<absent>", actual: b.Translate(locale: Locale.Es, key: "absent"));
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
        Assert.Equal(expected: "Hola", actual: strings["greeting"]);
        Assert.True(strings.Contains("only_en")); // via fallback
        Assert.False(strings.Contains("absent"));
    }
}
