namespace Zigote.UI.Localizations.Tests;

/// <summary>
///     The "framework supports the main languages" gate: exact CLDR cardinal categories for the
///     top world languages (by speakers), ordinals, RTL detection, locale parsing/formatting
///     robustness across the whole set, and the <see cref="PluralRules.Register" /> /
///     <see cref="TextDirectionInfo.RegisterRtlLanguage" /> extension seams a developer uses to add
///     a language the built-in tables don't cover.
/// </summary>
public class MainLanguageCoverageTests
{
    private static PluralCategory Card(string lang, double n, int? fractionDigits = null) =>
        PluralRules.Cardinal(
            language: lang,
            op: PluralOperands.FromDouble(value: n, fractionDigits: fractionDigits)
        );

    // ── Cardinal plurals across the main languages ────────────────────────────

    [Theory]
    // English-like (one: i=1 & v=0): en, de, nl, sv, fi, et, ur, sw, nb (via "no")
    [InlineData("en", 1, PluralCategory.One)]
    [InlineData("en", 2, PluralCategory.Other)]
    [InlineData("de", 1, PluralCategory.One)]
    [InlineData("nl", 2, PluralCategory.Other)]
    [InlineData("ur", 1, PluralCategory.One)]
    [InlineData("sw", 1, PluralCategory.One)]
    [InlineData("no", 1, PluralCategory.One)]
    // Romance
    [InlineData("es", 1, PluralCategory.One)]
    [InlineData("es", 2, PluralCategory.Other)]
    [InlineData("es", 1_000_000, PluralCategory.Many)]
    [InlineData("fr", 0, PluralCategory.One)]
    [InlineData("fr", 1, PluralCategory.One)]
    [InlineData("fr", 2, PluralCategory.Other)]
    [InlineData("pt", 0, PluralCategory.One)]
    [InlineData("pt", 2, PluralCategory.Other)]
    [InlineData("it", 1, PluralCategory.One)]
    [InlineData("it", 1_000_000, PluralCategory.Many)]
    // n = 1 languages: Turkic, Greek, Hungarian, Dravidian, Marathi, Nepali
    [InlineData("tr", 1, PluralCategory.One)]
    [InlineData("tr", 2, PluralCategory.Other)]
    [InlineData("hu", 1, PluralCategory.One)]
    [InlineData("el", 1, PluralCategory.One)]
    [InlineData("ta", 1, PluralCategory.One)]
    [InlineData("te", 2, PluralCategory.Other)]
    [InlineData("mr", 1, PluralCategory.One)]
    // Indic i=0 ∨ n=1
    [InlineData("hi", 0, PluralCategory.One)]
    [InlineData("hi", 1, PluralCategory.One)]
    [InlineData("hi", 2, PluralCategory.Other)]
    [InlineData("bn", 0, PluralCategory.One)]
    [InlineData("fa", 0, PluralCategory.One)]
    // Slavic one/few/many
    [InlineData("ru", 1, PluralCategory.One)]
    [InlineData("ru", 2, PluralCategory.Few)]
    [InlineData("ru", 5, PluralCategory.Many)]
    [InlineData("ru", 11, PluralCategory.Many)]
    [InlineData("ru", 21, PluralCategory.One)]
    [InlineData("ru", 22, PluralCategory.Few)]
    [InlineData("uk", 5, PluralCategory.Many)]
    [InlineData("pl", 1, PluralCategory.One)]
    [InlineData("pl", 2, PluralCategory.Few)]
    [InlineData("pl", 12, PluralCategory.Many)]
    [InlineData("cs", 2, PluralCategory.Few)]
    [InlineData("cs", 5, PluralCategory.Other)]
    // Serbo-Croatian: like Russian but residual = other (no "many")
    [InlineData("sr", 1, PluralCategory.One)]
    [InlineData("sr", 2, PluralCategory.Few)]
    [InlineData("sr", 5, PluralCategory.Other)]
    [InlineData("sr", 11, PluralCategory.Other)]
    [InlineData("sr", 21, PluralCategory.One)]
    [InlineData("hr", 22, PluralCategory.Few)]
    [InlineData("bs", 12, PluralCategory.Other)]
    [InlineData("sh", 21, PluralCategory.One)]
    // Baltic / Romanian
    [InlineData("lt", 1, PluralCategory.One)]
    [InlineData("lt", 2, PluralCategory.Few)]
    [InlineData("lt", 10, PluralCategory.Other)]
    [InlineData("ro", 0, PluralCategory.Few)]
    [InlineData("ro", 1, PluralCategory.One)]
    [InlineData("ro", 20, PluralCategory.Other)]
    // Filipino: "one" unless a relevant digit is 4, 6 or 9
    [InlineData("fil", 1, PluralCategory.One)]
    [InlineData("fil", 2, PluralCategory.One)]
    [InlineData("fil", 4, PluralCategory.Other)]
    [InlineData("fil", 6, PluralCategory.Other)]
    [InlineData("fil", 9, PluralCategory.Other)]
    [InlineData("fil", 10, PluralCategory.One)]
    [InlineData("tl", 14, PluralCategory.Other)]
    // Arabic: all six categories
    [InlineData("ar", 0, PluralCategory.Zero)]
    [InlineData("ar", 1, PluralCategory.One)]
    [InlineData("ar", 2, PluralCategory.Two)]
    [InlineData("ar", 3, PluralCategory.Few)]
    [InlineData("ar", 11, PluralCategory.Many)]
    [InlineData("ar", 100, PluralCategory.Other)]
    // Hebrew (incl. the legacy "iw" code)
    [InlineData("he", 1, PluralCategory.One)]
    [InlineData("he", 2, PluralCategory.Two)]
    [InlineData("he", 20, PluralCategory.Many)]
    [InlineData("he", 3, PluralCategory.Other)]
    [InlineData("iw", 2, PluralCategory.Two)]
    // No plural distinction
    [InlineData("ja", 1, PluralCategory.Other)]
    [InlineData("ko", 1, PluralCategory.Other)]
    [InlineData("zh", 1, PluralCategory.Other)]
    [InlineData("vi", 1, PluralCategory.Other)]
    [InlineData("th", 1, PluralCategory.Other)]
    [InlineData("id", 1, PluralCategory.Other)]
    [InlineData("in", 1, PluralCategory.Other)]
    [InlineData("ms", 1, PluralCategory.Other)]
    public void Cardinal_matches_cldr(string lang, double n, PluralCategory expected) =>
        Assert.Equal(expected: expected, actual: Card(lang: lang, n: n));

    [Theory]
    [InlineData("ru", PluralCategory.Other)] // 1.5 файла
    [InlineData("cs", PluralCategory.Many)] // 1,5 dne
    [InlineData("da", PluralCategory.One)] // 1,5 time — t != 0 and i is 0 or 1
    public void Fractions_select_the_right_category(string lang, PluralCategory expected) =>
        Assert.Equal(expected: expected, actual: Card(lang: lang, n: 1.5));

    [Fact]
    public void Danish_half_is_singular()
    {
        Assert.Equal(expected: PluralCategory.One, actual: Card(lang: "da", n: 0.5));
        Assert.Equal(expected: PluralCategory.Other, actual: Card(lang: "da", n: 2));
    }

    // ── Ordinals ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("en", 1, PluralCategory.One)] // 1st
    [InlineData("en", 2, PluralCategory.Two)] // 2nd
    [InlineData("en", 3, PluralCategory.Few)] // 3rd
    [InlineData("en", 4, PluralCategory.Other)] // 4th
    [InlineData("en", 11, PluralCategory.Other)] // 11th
    [InlineData("en", 21, PluralCategory.One)] // 21st
    [InlineData("sv", 1, PluralCategory.One)]
    [InlineData("sv", 2, PluralCategory.One)]
    [InlineData("sv", 11, PluralCategory.Other)]
    [InlineData("ru", 1, PluralCategory.Other)]
    [InlineData("ja", 1, PluralCategory.Other)]
    public void Ordinal_matches_cldr(string lang, long n, PluralCategory expected) => Assert.Equal(
        expected: expected,
        actual: PluralRules.Ordinal(language: lang, op: PluralOperands.FromLong(n))
    );

    // ── RTL detection ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ar", TextDirection.Rtl)]
    [InlineData("he", TextDirection.Rtl)]
    [InlineData("fa", TextDirection.Rtl)]
    [InlineData("ur", TextDirection.Rtl)]
    [InlineData("ps", TextDirection.Rtl)]
    [InlineData("en", TextDirection.Ltr)]
    [InlineData("ru", TextDirection.Ltr)]
    [InlineData("zh", TextDirection.Ltr)]
    [InlineData("hi", TextDirection.Ltr)]
    public void Direction_by_language(string lang, TextDirection expected) => Assert.Equal(
        expected: expected,
        actual: TextDirectionInfo.ForLanguage(lang)
    );

    [Fact]
    public void Script_subtag_overrides_language_direction()
    {
        Assert.Equal(
            expected: TextDirection.Rtl,
            actual: TextDirectionInfo.ForLanguage(language: "az", script: "Arab")
        );
        Assert.Equal(expected: TextDirection.Ltr, actual: TextDirectionInfo.ForLanguage("az"));
    }

    // ── Parsing + formatting never throw for any main language ────────────────

    [Theory]
    [InlineData("en")]
    [InlineData("zh")]
    [InlineData("hi")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ar")]
    [InlineData("bn")]
    [InlineData("pt")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("de")]
    [InlineData("ko")]
    [InlineData("tr")]
    [InlineData("it")]
    [InlineData("vi")]
    [InlineData("ta")]
    [InlineData("te")]
    [InlineData("mr")]
    [InlineData("ur")]
    [InlineData("fa")]
    [InlineData("pl")]
    [InlineData("uk")]
    [InlineData("nl")]
    [InlineData("th")]
    [InlineData("id")]
    [InlineData("he")]
    [InlineData("sv")]
    [InlineData("el")]
    [InlineData("hu")]
    [InlineData("fil")]
    [InlineData("sr")]
    [InlineData("sw")]
    [InlineData("zh-Hant-TW")]
    [InlineData("pt-BR")]
    public void Locale_parses_and_formats_for_every_main_language(string tag)
    {
        var locale = Locale.Parse(tag);
        Assert.False(locale.IsEmpty);

        var fmt = LocaleFormatting.For(locale);
        Assert.False(string.IsNullOrEmpty(fmt.Number(1234567.89)));
        Assert.False(string.IsNullOrEmpty(fmt.Currency(value: 9.99m, currencyCode: "USD")));
        Assert.False(string.IsNullOrEmpty(fmt.Date(new DateTime(year: 2026, month: 7, day: 5))));

        // Plural selection must resolve to a category for any language, listed or not.
        var category = PluralRules.Cardinal(
            language: locale.Language,
            op: PluralOperands.FromLong(3)
        );
        Assert.True(Enum.IsDefined(category));
    }

    // ── Developer extension seams ─────────────────────────────────────────────

    [Fact]
    public void Registered_plural_rule_overrides_builtin_end_to_end()
    {
        // A made-up language code so the test never collides with a built-in rule.
        PluralRules.Register(
            language: "zxx",
            cardinal: op => op.N == 2 ? PluralCategory.Two : PluralCategory.Other
        );
        try
        {
            var locale = Locale.Parse("zxx");
            string pattern = "{n, plural, two {a pair} other {# things}}";

            // The custom rule routes 2 → "two" through the whole MessageFormat pipeline…
            Assert.Equal(
                expected: "a pair",
                actual: new MessageFormat(pattern).Format(locale: locale, ("n", 2))
            );
            Assert.Equal(
                expected: "3 things",
                actual: new MessageFormat(pattern).Format(locale: locale, ("n", 3))
            );
        }
        finally
        {
            Assert.True(PluralRules.Unregister("zxx"));
        }

        // …and unregistering restores the default English-like shape (2 → other).
        Assert.Equal(
            expected: PluralCategory.Other,
            actual: PluralRules.Cardinal(language: "zxx", op: PluralOperands.FromLong(2))
        );
    }

    [Fact]
    public void Registered_ordinal_rule_is_used()
    {
        PluralRules.Register(
            language: "zxx",
            cardinal: null,
            ordinal: op => op.N == 1 ? PluralCategory.One : PluralCategory.Other
        );
        try
        {
            Assert.Equal(
                expected: PluralCategory.One,
                actual: PluralRules.Ordinal(language: "zxx", op: PluralOperands.FromLong(1))
            );
            // Cardinal was not overridden — the built-in fallback still applies.
            Assert.Equal(
                expected: PluralCategory.One,
                actual: PluralRules.Cardinal(language: "zxx", op: PluralOperands.FromLong(1))
            );
        }
        finally
        {
            PluralRules.Unregister("zxx");
        }
    }

    [Fact]
    public void Registered_rtl_language_and_script_are_used()
    {
        // Private-use codes so the registration never affects other tests.
        TextDirectionInfo.RegisterRtlLanguage("qaa");
        TextDirectionInfo.RegisterRtlScript("Qaaa");

        Assert.Equal(expected: TextDirection.Rtl, actual: TextDirectionInfo.ForLanguage("qaa"));
        Assert.Equal(
            expected: TextDirection.Rtl,
            actual: TextDirectionInfo.ForLanguage(language: "en", script: "Qaaa")
        );

        // Registration also flows into Locale.TextDirection (evaluated per read).
        Assert.Equal(expected: TextDirection.Rtl, actual: Locale.Parse("qaa").TextDirection);
    }
}
