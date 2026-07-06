using Zigote.Core;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Localizations.Tests;

// Regressions locking in the fixes from the adversarial review of 2026-07-03.
public class ReviewRegressionTests
{
    // Finding: Romance family gained a CLDR 'many' category at exact millions.
    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("pt")]
    [InlineData("it")]
    [InlineData("ca")]
    public void Romance_many_at_exact_millions(string lang)
    {
        Assert.Equal(
            PluralCategory.Many,
            PluralRules.Cardinal(lang, PluralOperands.FromLong(1_000_000))
        );
        Assert.Equal(
            PluralCategory.Many,
            PluralRules.Cardinal(lang, PluralOperands.FromLong(2_000_000))
        );
        Assert.Equal(
            PluralCategory.Other,
            PluralRules.Cardinal(lang, PluralOperands.FromLong(1_500_000))
        );
        Assert.Equal(PluralCategory.Other, PluralRules.Cardinal(lang, PluralOperands.FromLong(5)));
    }

    [Fact]
    public void Romance_one_rules_unchanged()
    {
        Assert.Equal(PluralCategory.One, PluralRules.Cardinal("es", PluralOperands.FromLong(1)));
        Assert.Equal(PluralCategory.One, PluralRules.Cardinal("fr", PluralOperands.FromLong(0)));
        Assert.Equal(PluralCategory.One, PluralRules.Cardinal("it", PluralOperands.FromLong(1)));
        Assert.Equal(PluralCategory.Other, PluralRules.Cardinal("es", PluralOperands.FromLong(2)));
    }

    // Finding: Finnish has no ordinal rule — must be 'other', not Swedish's 'one'.
    [Fact]
    public void Finnish_ordinal_is_other()
    {
        foreach (var n in new[] {
                     1,
                     2,
                     21,
                     22,
                 })
            Assert.Equal(
                PluralCategory.Other,
                PluralRules.Ordinal("fi", PluralOperands.FromLong(n))
            );
        // Swedish keeps its rule.
        Assert.Equal(PluralCategory.One, PluralRules.Ordinal("sv", PluralOperands.FromLong(1)));
    }

    // Finding: huge integers with all-zero low digits must not collapse i to 0.
    [Fact]
    public void Huge_integer_stays_nonzero_for_i_tests()
    {
        // hi: one iff i==0 or n==1. 1e19 has i != 0 and n != 1 -> other.
        Assert.Equal(
            PluralCategory.Other,
            PluralRules.Cardinal("hi", PluralOperands.FromDouble(1e19))
        );
        // fr Romance many: i % 1e6 == 0 and i != 0 -> many.
        Assert.Equal(
            PluralCategory.Many,
            PluralRules.Cardinal("fr", PluralOperands.FromDouble(1e19))
        );
    }

    // Finding: Hausa and Kurdish (macrolanguage) default to Latin/LTR.
    [Fact]
    public void Hausa_and_Kurdish_default_ltr()
    {
        Assert.Equal(TextDirection.Ltr, Locale.Parse("ha").TextDirection);
        Assert.Equal(TextDirection.Ltr, Locale.Parse("ku").TextDirection);
        // RTL variants still resolve via script / Sorani.
        Assert.Equal(TextDirection.Rtl, Locale.Parse("ha-Arab").TextDirection);
        Assert.Equal(TextDirection.Rtl, Locale.Parse("ckb").TextDirection);
    }

    // Finding: a script subtag appearing after a region was silently dropped.
    [Theory]
    [InlineData(
        "zh-TW-Hant",
        "zh",
        "Hant",
        "TW"
    )]
    [InlineData(
        "sr-RS-Latn",
        "sr",
        "Latn",
        "RS"
    )]
    public void Script_after_region_is_preserved(string tag, string lang, string script,
        string country)
    {
        var l = Locale.Parse(tag);
        Assert.Equal(lang, l.Language);
        Assert.Equal(script, l.Script);
        Assert.Equal(country, l.Country);
    }

    // Finding: formatting a default(Locale) returned null (NRE risk).
    [Fact]
    public void Default_locale_formats_to_empty_string_not_null()
    {
        Locale empty = default;
        Assert.Equal("", empty.ToBcp47());
        Assert.Equal("", empty.ToUnderscore());
        Assert.Equal(0, empty.ToBcp47().Length); // no NRE
    }

    // Finding: a region match must not beat a requested script.
    [Fact]
    public void Requested_script_beats_region_match()
    {
        var supported = new[] {
            Locale.Parse("zh-CN"),
            Locale.Parse("zh-Hant-TW"),
        };
        Assert.Equal(
            Locale.Parse("zh-Hant-TW"),
            LocaleResolution.Resolve(Locale.Parse("zh-Hant-CN"), supported, Locale.En)
        );

        var supported2 = new[] {
            Locale.Parse("zh-TW"),
            Locale.Parse("zh-Hans-CN"),
        };
        Assert.Equal(
            Locale.Parse("zh-Hans-CN"),
            LocaleResolution.Resolve(Locale.Parse("zh-Hans-TW"), supported2, Locale.En)
        );
    }
}

// Scope + controller smoke tests (no engine window needed: the retained provider subtree measures a
// plain SizedBox child, and the controller is exercised directly).
public class LocalizationsScopeTests
{
    private static LocalizationsScope BuildScope()
    {
        var bundle = new LocalizationBundle(
            new LocalizationCatalog(Locale.En) { ["hi"] = "Hello" },
            new LocalizationCatalog(Locale.Es) { ["hi"] = "Hola" }
        );

        var scope = new LocalizationsScope {
            Bundle = bundle,
            SupportedLocales = {
                Locale.En,
                Locale.Es,
            },
            InitialLocale = Locale.Es,
            UseSystemLocale = false,
            Child = new SizedBox(10f, 10f),
        };
        scope.Measure(Constraints.Tight(100f, 100f)); // triggers one-time build
        return scope;
    }

    [Fact]
    public void Builds_controller_at_resolved_initial_locale()
    {
        var scope = BuildScope();
        Assert.NotNull(scope.Controller);
        Assert.Equal(Locale.Es, scope.Controller!.Locale);
        Assert.Single(scope.GetChildren());
    }

    [Fact]
    public void Controller_loads_string_payload_and_direction()
    {
        var scope = BuildScope();
        var es = scope.Controller!.Load(Locale.Es);
        Assert.Equal("Hola", es.Get<StringLocalizations>()!["hi"]);
        Assert.Equal(TextDirection.Ltr, es.TextDirection);
        Assert.Equal(TextDirection.Rtl, scope.Controller.Load(Locale.Ar).TextDirection);
    }

    [Fact]
    public void SetLocale_switches_and_is_idempotent()
    {
        var scope = BuildScope();
        var c = scope.Controller!;
        Locale? observed = null;
        c.LocaleChanged += l => observed = l;

        Assert.True(c.SetLocale(Locale.En));
        Assert.Equal(Locale.En, c.Locale);
        Assert.Equal(Locale.En, observed);
        Assert.Equal(Locale.En, c.Current.Value);

        Assert.False(c.SetLocale(Locale.En)); // no-op
    }

    [Fact]
    public void Unsupported_requested_locale_resolves_to_fallback()
    {
        var scope = BuildScope();
        // ja is unsupported -> resolves within {en, es}, fallback es.
        scope.Controller!.SetLocale(Locale.Ja);
        Assert.Contains(
            scope.Controller.Locale,
            new[] {
                Locale.En,
                Locale.Es,
            }
        );
    }
}