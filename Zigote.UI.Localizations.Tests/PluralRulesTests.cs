namespace Zigote.UI.Localizations.Tests;

public class PluralOperandsTests
{
    [Fact]
    public void Integer_operands()
    {
        var op = PluralOperands.FromLong(11);
        Assert.Equal(expected: 11, actual: op.I);
        Assert.Equal(expected: 0, actual: op.V);
        Assert.Equal(expected: 0, actual: op.F);
        Assert.Equal(expected: 11d, actual: op.N);
    }

    [Fact]
    public void Fraction_operands_trim_trailing_zeros_for_w_t()
    {
        var op = PluralOperands.FromDouble(value: 1.50, fractionDigits: 2); // "1.50"
        Assert.Equal(expected: 1, actual: op.I);
        Assert.Equal(expected: 2, actual: op.V); // visible fraction digits WITH trailing zeros
        Assert.Equal(expected: 1, actual: op.W); // WITHOUT trailing zeros
        Assert.Equal(expected: 50, actual: op.F);
        Assert.Equal(expected: 5, actual: op.T);
    }

    [Fact]
    public void Bare_double_has_no_visible_fraction_when_integral()
    {
        var op = PluralOperands.FromDouble(2.0);
        Assert.Equal(expected: 2, actual: op.I);
        Assert.Equal(expected: 0, actual: op.V);
    }

    [Fact]
    public void Negative_values_use_absolute_operands()
    {
        var op = PluralOperands.FromDouble(-3.5);
        Assert.Equal(expected: 3, actual: op.I);
        Assert.Equal(expected: 3.5d, actual: op.N);
        Assert.Equal(expected: 1, actual: op.V);
        Assert.Equal(expected: 5, actual: op.F);
    }
}

public class PluralRulesCardinalTests
{
    private static PluralCategory Card(string lang, double n) => PluralRules.Cardinal(
        language: lang,
        op: PluralOperands.FromDouble(n)
    );

    [Theory]
    [InlineData(0, PluralCategory.Other)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    [InlineData(5, PluralCategory.Other)]
    [InlineData(100, PluralCategory.Other)]
    public void English(int n, PluralCategory expected)
    {
        Assert.Equal(expected: expected, actual: Card(lang: "en", n: n));
        Assert.Equal(expected: expected, actual: Card(lang: "de", n: n)); // same rule family
    }

    [Fact]
    public void English_displayed_fraction_is_other()
    {
        Assert.Equal(
            expected: PluralCategory.Other,
            actual: PluralRules.Cardinal(
                language: "en",
                op: PluralOperands.FromDouble(value: 1.0, fractionDigits: 1)
            )
        );
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(11, PluralCategory.Many)]
    [InlineData(21, PluralCategory.One)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(25, PluralCategory.Many)]
    [InlineData(100, PluralCategory.Many)]
    [InlineData(0, PluralCategory.Many)]
    public void Russian(int n, PluralCategory expected)
    {
        Assert.Equal(expected: expected, actual: Card(lang: "ru", n: n));
        Assert.Equal(expected: expected, actual: Card(lang: "uk", n: n));
    }

    [Fact]
    public void Russian_fraction_is_other() => Assert.Equal(
        expected: PluralCategory.Other,
        actual: Card(lang: "ru", n: 1.5)
    );

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(3, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(12, PluralCategory.Many)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(0, PluralCategory.Many)]
    public void Polish(int n, PluralCategory expected) => Assert.Equal(
        expected: expected,
        actual: Card(lang: "pl", n: n)
    );

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Other)]
    [InlineData(0, PluralCategory.Other)]
    public void Czech(int n, PluralCategory expected)
    {
        Assert.Equal(expected: expected, actual: Card(lang: "cs", n: n));
        Assert.Equal(expected: expected, actual: Card(lang: "sk", n: n));
    }

    [Fact]
    public void Czech_fraction_is_many() => Assert.Equal(
        expected: PluralCategory.Many,
        actual: Card(lang: "cs", n: 1.5)
    );

    [Theory]
    [InlineData(0, PluralCategory.Zero)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Two)]
    [InlineData(3, PluralCategory.Few)]
    [InlineData(10, PluralCategory.Few)]
    [InlineData(11, PluralCategory.Many)]
    [InlineData(99, PluralCategory.Many)]
    [InlineData(100, PluralCategory.Other)]
    [InlineData(103, PluralCategory.Few)]
    public void Arabic(int n, PluralCategory expected) => Assert.Equal(
        expected: expected,
        actual: Card(lang: "ar", n: n)
    );

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Two)]
    [InlineData(3, PluralCategory.Other)]
    [InlineData(10, PluralCategory.Other)]
    [InlineData(20, PluralCategory.Many)]
    [InlineData(30, PluralCategory.Many)]
    public void Hebrew(int n, PluralCategory expected) => Assert.Equal(
        expected: expected,
        actual: Card(lang: "he", n: n)
    );

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(21, PluralCategory.One)]
    [InlineData(11, PluralCategory.Other)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(9, PluralCategory.Few)]
    [InlineData(12, PluralCategory.Other)]
    [InlineData(10, PluralCategory.Other)]
    public void Lithuanian(int n, PluralCategory expected) => Assert.Equal(
        expected: expected,
        actual: Card(lang: "lt", n: n)
    );

    [Fact]
    public void Lithuanian_fraction_is_many() => Assert.Equal(
        expected: PluralCategory.Many,
        actual: Card(lang: "lt", n: 1.5)
    );

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(19, PluralCategory.Few)]
    [InlineData(20, PluralCategory.Other)]
    [InlineData(101, PluralCategory.Other)]
    [InlineData(0, PluralCategory.Few)]
    public void Romanian(int n, PluralCategory expected) => Assert.Equal(
        expected: expected,
        actual: Card(lang: "ro", n: n)
    );

    [Theory]
    [InlineData(0, PluralCategory.One)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void French_and_Portuguese(int n, PluralCategory expected)
    {
        Assert.Equal(expected: expected, actual: Card(lang: "fr", n: n));
        Assert.Equal(expected: expected, actual: Card(lang: "pt", n: n));
    }

    [Fact]
    public void French_fraction_with_integer_one_is_one() => Assert.Equal(
        expected: PluralCategory.One,
        actual: Card(lang: "fr", n: 1.5)
    ); // i = 1 -> one

    [Theory]
    [InlineData(0, PluralCategory.Other)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void Spanish_and_Turkish(int n, PluralCategory expected)
    {
        Assert.Equal(expected: expected, actual: Card(lang: "es", n: n));
        Assert.Equal(expected: expected, actual: Card(lang: "tr", n: n));
    }

    [Theory]
    [InlineData(0, PluralCategory.One)] // i = 0 -> one
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void Hindi(int n, PluralCategory expected) => Assert.Equal(
        expected: expected,
        actual: Card(lang: "hi", n: n)
    );

    [Theory]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("ko")]
    [InlineData("vi")]
    [InlineData("th")]
    public void No_plural_languages_are_always_other(string lang)
    {
        foreach (int n in new[] {
                     0,
                     1,
                     2,
                     5,
                     11,
                     100,
                 })
            Assert.Equal(expected: PluralCategory.Other, actual: Card(lang: lang, n: n));
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void Unknown_language_defaults_to_english_like(int n, PluralCategory expected) =>
        Assert.Equal(expected: expected, actual: Card(lang: "xyz", n: n));
}

public class PluralRulesOrdinalTests
{
    private static PluralCategory Ord(string lang, long n) => PluralRules.Ordinal(
        language: lang,
        op: PluralOperands.FromLong(n)
    );

    [Theory]
    [InlineData(1, PluralCategory.One)] // 1st
    [InlineData(2, PluralCategory.Two)] // 2nd
    [InlineData(3, PluralCategory.Few)] // 3rd
    [InlineData(4, PluralCategory.Other)] // 4th
    [InlineData(11, PluralCategory.Other)] // 11th
    [InlineData(12, PluralCategory.Other)]
    [InlineData(13, PluralCategory.Other)]
    [InlineData(21, PluralCategory.One)] // 21st
    [InlineData(22, PluralCategory.Two)]
    [InlineData(23, PluralCategory.Few)]
    [InlineData(101, PluralCategory.One)]
    [InlineData(111, PluralCategory.Other)]
    public void English_ordinal(int n, PluralCategory expected) => Assert.Equal(
        expected: expected,
        actual: Ord(lang: "en", n: n)
    );

    [Fact]
    public void Default_ordinal_is_other()
    {
        foreach (int n in new[] {
                     1,
                     2,
                     3,
                     4,
                     11,
                     21,
                 })
            Assert.Equal(expected: PluralCategory.Other, actual: Ord(lang: "ru", n: n));
    }
}
