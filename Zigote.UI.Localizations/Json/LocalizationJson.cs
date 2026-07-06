using System.Text.Json;

namespace Zigote.UI.Localizations;

/// <summary>
///     Loads translation catalogs from JSON — a flat <c>{ "key": "template" }</c> document (one per
///     locale, ARB-compatible) or a nested <c>{ "en": { … }, "es": { … } }</c> multi-locale document.
///     Parsed with <see cref="JsonDocument" /> (a DOM walk — no reflection, so trim/AOT-safe): only
///     string-valued entries become messages, and ARB metadata keys (those beginning with <c>@</c>,
///     whose values are objects) are skipped. The document root must be a JSON object.
/// </summary>
public static class LocalizationJson
{
    private static readonly JsonDocumentOptions Options = new() {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    ///     Parse a flat <c>{ "key": "template" }</c> document into a catalog for
    ///     <paramref name="locale" />.
    /// </summary>
    public static LocalizationCatalog LoadCatalog(string json, Locale locale)
    {
        using var doc = JsonDocument.Parse(json, Options);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Localization JSON must be a JSON object.");
        return ReadCatalog(locale, doc.RootElement);
    }

    /// <summary>Parse a nested <c>{ "&lt;locale&gt;": { "key": "template" } }</c> document into a bundle.</summary>
    public static LocalizationBundle LoadBundle(string json)
    {
        using var doc = JsonDocument.Parse(json, Options);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Localization JSON must be a JSON object.");

        var bundle = new LocalizationBundle();
        foreach (var localeProp in doc.RootElement.EnumerateObject())
        {
            if (localeProp.Value.ValueKind != JsonValueKind.Object) continue;
            if (!Locale.TryParse(localeProp.Name, out var locale)) continue;
            bundle.Add(ReadCatalog(locale, localeProp.Value));
        }

        return bundle;
    }

    private static LocalizationCatalog ReadCatalog(Locale locale, JsonElement obj)
    {
        var catalog = new LocalizationCatalog(locale);
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Length > 0 && prop.Name[0] == '@')
                continue; // ARB metadata (@@locale, @key)
            if (prop.Value.ValueKind == JsonValueKind.String)
                catalog.Add(prop.Name, prop.Value.GetString()!);
        }

        return catalog;
    }
}