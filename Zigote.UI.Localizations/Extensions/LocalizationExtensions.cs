namespace Zigote.UI.Localizations;

/// <summary>
///     Ergonomic <see cref="BuildContext" /> helpers for reading localization inside a widget's
///     <c>Build</c>. The lookups register a dependency on the ambient <see cref="Localizations" />, so
///     the calling widget rebuilds when the locale changes; the action helpers (
///     <see cref="SetLocale" />)
///     do not.
/// </summary>
public static class LocalizationExtensions
{
    private static readonly IReadOnlyDictionary<string, object?> NoArgs =
        new Dictionary<string, object?>(0);

    // ── Translation ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Translate a key with inline <c>(name, value)</c> arguments (via
    ///     <see cref="StringLocalizations" />).
    /// </summary>
    public static string Tr(this BuildContext context, string key,
        params (string Name, object? Value)[] args) =>
        context.Tr(key: key, args: args.Length == 0 ? NoArgs : MessageFormat.ToDictionary(args));

    /// <summary>Translate a key with a named-argument dictionary.</summary>
    public static string Tr(this BuildContext context, string key,
        IReadOnlyDictionary<string, object?> args)
    {
        var strings = context.DependOn<Localizations>()?.Data.Get<StringLocalizations>();
        return strings?.Translate(key: key, args: args) ?? key;
    }

    // ── Locale / direction ───────────────────────────────────────────────────

    /// <summary>The active locale (registers a dependency).</summary>
    public static Locale LocaleOf(this BuildContext context) =>
        context.DependOn<Localizations>()?.Data.Locale ?? Locale.En;

    /// <summary>The active text direction (registers a dependency).</summary>
    public static TextDirection TextDirectionOf(this BuildContext context) =>
        context.DependOn<Localizations>()?.Data.TextDirection ?? TextDirection.Ltr;

    /// <summary>The localization controller in scope (no dependency), or <c>null</c>.</summary>
    public static LocalizationsController? LocalizationController(this BuildContext context) =>
        Localizations.ControllerOf(context);

    /// <summary>
    ///     Switch the app's locale at runtime. Returns <c>false</c> when there is no scope or it is a
    ///     no-op.
    /// </summary>
    public static bool SetLocale(this BuildContext context, Locale locale) =>
        Localizations.ControllerOf(context)?.SetLocale(locale) ?? false;

    // ── Formatting (locale-aware) ────────────────────────────────────────────

    /// <summary>The culture-aware formatter for the active locale (registers a dependency).</summary>
    public static LocaleFormatting Formatting(this BuildContext context) =>
        LocaleFormatting.For(context.LocaleOf());

    public static string FormatNumber(this BuildContext context, double value,
        string? pattern = null) =>
        context.Formatting().Number(value: value, pattern: pattern);

    public static string FormatInteger(this BuildContext context, long value) =>
        context.Formatting().Integer(value);

    public static string FormatPercent(this BuildContext context, double value, int decimals = 0) =>
        context.Formatting().Percent(value: value, decimals: decimals);

    public static string FormatCurrency(this BuildContext context, decimal value,
        string? currencyCode = null) =>
        context.Formatting().Currency(value: value, currencyCode: currencyCode);

    public static string FormatDate(this BuildContext context, DateTime value,
        DateStyle style = DateStyle.Medium) =>
        context.Formatting().Date(value: value, style: style);

    public static string FormatTime(this BuildContext context, DateTime value,
        DateStyle style = DateStyle.Short) =>
        context.Formatting().Time(value: value, style: style);
}
