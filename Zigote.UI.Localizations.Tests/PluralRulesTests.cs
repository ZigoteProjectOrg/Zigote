namespace Zigote.UI.Localizations.Tests;

public class PluralOperandsTests
{
    [Fact]
    public void Integer_operands()
    {
        var op = PluralOperands.FromLong(11);
        Assert.Equal(11, op.I);
        Assert.Equal(0, op.V);
        Assert.Equal(0, op.F);
        Assert.Equal(11d, op.N);
    }

    [Fact]
    public void Fraction_operands_trim_trailing_zeros_for_w_t()
    {
        var op = PluralOperands.FromDouble(1.50, 2); // "1.50"
        Assert.Equal(1, op.I);
        Assert.Equal(2, op.V); // visible fraction digits WITH trailing zeros
        Assert.Equal(1, op.W); // WITHOUT trailing zeros
        Assert.Equal(50, op.F);
        Assert.Equal(5, op.T);
    }

    [Fact]
    public void Bare_double_has_no_visible_fraction_when_integral()
    {
        var op = PluralOperands.FromDouble(2.0);
        Assert.Equal(2, op.I);
        Assert.Equal(0, op.V);
    }

    [Fact]
    public void Negative_values_use_absolute_operands()
    {
        var op = PluralOperands.FromDouble(-3.5);
        Assert.Equal(3, op.I);
        Assert.Equal(3.5d, op.N);
        Assert.Equal(1, op.V);
        Assert.Equal(5, op.F);
    }
}

public class PluralRulesCardinalTests
{
    private static PluralCategory Card(string lang, double n)
    {
        return PluralRules.Cardinal(lang, PluralOperands.FromDouble(n));
    }

    [Theory]
    [InlineData(0, PluralCategory.Other)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    [InlineData(5, PluralCategory.Other)]
    [InlineData(100, PluralCategory.Other)]
    public void English(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("en", n));
        Assert.Equal(expected, Card("de", n)); // same rule family
    }

    [Fact]
    public void English_displayed_fraction_is_other()
    {
        Assert.Equal(
            PluralCategory.Other,
            PluralRules.Cardinal("en", PluralOperands.FromDouble(1.0, 1))
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
        Assert.Equal(expected, Card("ru", n));
        Assert.Equal(expected, Card("uk", n));
    }

    [Fact]
    public void Russian_fraction_is_other()
    {
        Assert.Equal(PluralCategory.Other, Card("ru", 1.5));
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(3, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(12, PluralCategory.Many)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(0, PluralCategory.Many)]
    public void Polish(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("pl", n));
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Other)]
    [InlineData(0, PluralCategory.Other)]
    public void Czech(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("cs", n));
        Assert.Equal(expected, Card("sk", n));
    }

    [Fact]
    public void Czech_fraction_is_many()
    {
        Assert.Equal(PluralCategory.Many, Card("cs", 1.5));
    }

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
    public void Arabic(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("ar", n));
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Two)]
    [InlineData(3, PluralCategory.Other)]
    [InlineData(10, PluralCategory.Other)]
    [InlineData(20, PluralCategory.Many)]
    [InlineData(30, PluralCategory.Many)]
    public void Hebrew(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("he", n));
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(21, PluralCategory.One)]
    [InlineData(11, PluralCategory.Other)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(9, PluralCategory.Few)]
    [InlineData(12, PluralCategory.Other)]
    [InlineData(10, PluralCategory.Other)]
    public void Lithuanian(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("lt", n));
    }

    [Fact]
    public void Lithuanian_fraction_is_many()
    {
        Assert.Equal(PluralCategory.Many, Card("lt", 1.5));
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(19, PluralCategory.Few)]
    [InlineData(20, PluralCategory.Other)]
    [InlineData(101, PluralCategory.Other)]
    [InlineData(0, PluralCategory.Few)]
    public void Romanian(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("ro", n));
    }

    [Theory]
    [InlineData(0, PluralCategory.One)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void French_and_Portuguese(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("fr", n));
        Assert.Equal(expected, Card("pt", n));
    }

    [Fact]
    public void French_fraction_with_integer_one_is_one()
    {
        Assert.Equal(PluralCategory.One, Card("fr", 1.5)); // i = 1 -> one
    }

    [Theory]
    [InlineData(0, PluralCategory.Other)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void Spanish_and_Turkish(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("es", n));
        Assert.Equal(expected, Card("tr", n));
    }

    [Theory]
    [InlineData(0, PluralCategory.One)] // i = 0 -> one
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void Hindi(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("hi", n));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("ko")]
    [InlineData("vi")]
    [InlineData("th")]
    public void No_plural_languages_are_always_other(string lang)
    {
        foreach (var n in new[] {
                     0,
                     1,
                     2,
                     5,
                     11,
                     100,
                 })
            Assert.Equal(PluralCategory.Other, Card(lang, n));
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    public void Unknown_language_defaults_to_english_like(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Card("xyz", n));
    }
}

public class PluralRulesOrdinalTests
{
    private static PluralCategory Ord(string lang, long n)
    {
        return PluralRules.Ordinal(lang, PluralOperands.FromLong(n));
    }

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
    public void English_ordinal(int n, PluralCategory expected)
    {
        Assert.Equal(expected, Ord("en", n));
    }

    [Fact]
    public void Default_ordinal_is_other()
    {
        foreach (var n in new[] {
                     1,
                     2,
                     3,
                     4,
                     11,
                     21,
                 })
            Assert.Equal(PluralCategory.Other, Ord("ru", n));
    }
}