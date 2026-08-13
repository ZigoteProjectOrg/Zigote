namespace Zigote.UI.Localizations.Tests;

public class MessageFormatTests
{
    private static readonly Locale En = Locale.En;

    private static string Fmt(string pattern, params (string, object?)[] args) =>
        new MessageFormat(pattern).Format(locale: En, arguments: args);

    [Fact]
    public void Simple_placeholder() => Assert.Equal(
        expected: "Hello, World!",
        actual: Fmt(pattern: "Hello, {name}!", ("name", "World"))
    );

    [Fact]
    public void Missing_argument_is_shown_as_marker() => Assert.Equal(
        expected: "Hi {name}",
        actual: Fmt("Hi {name}")
    );

    [Fact]
    public void Plain_integer_argument_formats_without_surprises() => Assert.Equal(
        expected: "The answer is 42.",
        actual: Fmt(pattern: "The answer is {n}.", ("n", 42))
    );

    [Theory]
    [InlineData(0, "No items")]
    [InlineData(1, "1 item")]
    [InlineData(5, "5 items")]
    public void Plural_with_explicit_and_keyword_cases(int count, string expected)
    {
        Assert.Equal(
            expected: expected,
            actual: Fmt(
                pattern: "{count, plural, =0 {No items} one {# item} other {# items}}",
                ("count", count)
            )
        );
    }

    [Fact]
    public void Plural_russian_selects_few_and_many()
    {
        string p = "{n, plural, one {# файл} few {# файла} many {# файлов} other {# файла}}";
        Assert.Equal(
            expected: "1 файл",
            actual: new MessageFormat(p).Format(locale: Locale.Ru, ("n", 1))
        );
        Assert.Equal(
            expected: "2 файла",
            actual: new MessageFormat(p).Format(locale: Locale.Ru, ("n", 2))
        );
        Assert.Equal(
            expected: "5 файлов",
            actual: new MessageFormat(p).Format(locale: Locale.Ru, ("n", 5))
        );
    }

    [Fact]
    public void Plural_offset_subtracts_before_selection_and_pound()
    {
        string p = "{n, plural, offset:1 one {# person} other {# people}}";
        Assert.Equal(
            expected: "1 person",
            actual: Fmt(pattern: p, ("n", 2))
        ); // adjusted 1 -> one, # = 1
        Assert.Equal(
            expected: "2 people",
            actual: Fmt(pattern: p, ("n", 3))
        ); // adjusted 2 -> other, # = 2
    }

    [Fact]
    public void Plural_explicit_case_matches_raw_value_not_offset()
    {
        string p = "{n, plural, offset:1 =1 {only you} one {# person} other {# people}}";
        Assert.Equal(expected: "only you", actual: Fmt(pattern: p, ("n", 1)));
        Assert.Equal(expected: "1 person", actual: Fmt(pattern: p, ("n", 2)));
    }

    [Theory]
    [InlineData("male", "He")]
    [InlineData("female", "She")]
    [InlineData("nonbinary", "They")]
    public void Select(string gender, string expected)
    {
        Assert.Equal(
            expected: expected,
            actual: Fmt(pattern: "{g, select, male {He} female {She} other {They}}", ("g", gender))
        );
    }

    [Fact]
    public void Select_missing_argument_uses_other() => Assert.Equal(
        expected: "They",
        actual: Fmt("{g, select, male {He} other {They}}")
    );

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(21, "21st")]
    public void SelectOrdinal(int pos, string expected)
    {
        Assert.Equal(
            expected: expected,
            actual: Fmt(
                pattern: "{p, selectordinal, one {#st} two {#nd} few {#rd} other {#th}}",
                ("p", pos)
            )
        );
    }

    [Fact]
    public void Nested_plural_inside_select()
    {
        string p = "{g, select, other {{n, plural, one {# cat} other {# cats}}}}";
        Assert.Equal(expected: "1 cat", actual: Fmt(pattern: p, ("g", "x"), ("n", 1)));
        Assert.Equal(expected: "2 cats", actual: Fmt(pattern: p, ("g", "x"), ("n", 2)));
    }

    [Theory]
    [InlineData("it''s here", "it's here")]
    [InlineData("'{'quoted'}'", "{quoted}")]
    [InlineData("5 o''clock", "5 o'clock")]
    [InlineData("'{'", "{")]
    public void Apostrophe_escaping(string pattern, string expected) =>
        Assert.Equal(expected: expected, actual: Fmt(pattern));

    [Fact]
    public void Pound_outside_plural_is_literal() => Assert.Equal(
        expected: "C# rocks",
        actual: Fmt("C# rocks")
    );

    [Fact]
    public void Typed_number_integer_rounds_and_groups() => Assert.Equal(
        expected: "1,235",
        actual: Fmt(pattern: "{n, number, integer}", ("n", 1234.5))
    );

    [Fact]
    public void Typed_number_percent() => Assert.Contains(
        expectedSubstring: "50",
        actualString: Fmt(pattern: "{r, number, percent}", ("r", 0.5))
    );

    [Fact]
    public void Reuse_formats_many_times()
    {
        var mf = new MessageFormat("{count, plural, one {# item} other {# items}}");
        Assert.Equal(expected: "1 item", actual: mf.Format(locale: En, ("count", 1)));
        Assert.Equal(expected: "3 items", actual: mf.Format(locale: En, ("count", 3)));
    }

    // ── Robustness: malformed patterns never hang and throw only FormatException ──

    [Theory]
    [InlineData("{")]
    [InlineData("{x")]
    [InlineData("{x, plural, one {oops}")] // missing closing brace
    [InlineData("{x, plural, =")]
    public void Malformed_patterns_throw_FormatException(string pattern) =>
        Assert.Throws<FormatException>(() => new MessageFormat(pattern));

    [Fact]
    public void Empty_pattern_is_empty() => Assert.Equal(expected: "", actual: Fmt(""));

    [Fact]
    public void Plural_with_no_cases_yields_empty()
    {
        // Parses, selects nothing -> empty (no crash, no hang).
        Assert.Equal(expected: "", actual: Fmt(pattern: "{x, plural, }", ("x", 5)));
    }

    [Fact]
    public void Stray_closing_brace_is_literal() =>
        Assert.Equal(expected: "a}b", actual: Fmt("a}b"));
}
