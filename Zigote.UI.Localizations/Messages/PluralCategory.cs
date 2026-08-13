namespace Zigote.UI.Localizations;

/// <summary>
///     The CLDR plural categories. A language uses a subset; the ones it does not distinguish always
///     resolve to <see cref="Other" />. Both cardinal ("1 item / 2 items") and ordinal ("1st / 2nd")
///     selection produce one of these.
/// </summary>
public enum PluralCategory
{
    /// <summary>Explicit-zero forms (Latvian, Arabic, Welsh, …).</summary>
    Zero,

    /// <summary>Singular ("one item"). In English etc. this is <c>n == 1</c>.</summary>
    One,

    /// <summary>Dual ("two items"): Arabic, Hebrew, Slovenian, Welsh, …</summary>
    Two,

    /// <summary>Paucal / small-count form: Polish, Russian, Czech, Arabic, …</summary>
    Few,

    /// <summary>Large-count form: Polish, Russian, Arabic, …</summary>
    Many,

    /// <summary>The catch-all form every language has (English plural, and the default elsewhere).</summary>
    Other,
}

/// <summary>Parses the keyword form of a <see cref="PluralCategory" /> as it appears in a message.</summary>
public static class PluralCategoryNames
{
    public static bool TryParse(string keyword, out PluralCategory category)
    {
        switch (keyword)
        {
            case "zero":
                category = PluralCategory.Zero;
                return true;
            case "one":
                category = PluralCategory.One;
                return true;
            case "two":
                category = PluralCategory.Two;
                return true;
            case "few":
                category = PluralCategory.Few;
                return true;
            case "many":
                category = PluralCategory.Many;
                return true;
            case "other":
                category = PluralCategory.Other;
                return true;
            default:
                category = PluralCategory.Other;
                return false;
        }
    }
}
