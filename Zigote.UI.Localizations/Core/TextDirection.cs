namespace Zigote.UI.Localizations;

// TextDirection and the Directionality provider moved DOWN into base Zigote.UI
// (Zigote.UI/Widgets/Directionality.cs) so the layout primitives (Row/Wrap/Padding/RichText)
// can mirror for RTL — this project supplies the locale→direction knowledge on top.

/// <summary>
///     Maps a language/script to its writing direction. The built-in RTL sets cover every current
///     RTL language/script; a niche or new code can be added at startup with
///     <see cref="RegisterRtlLanguage" /> / <see cref="RegisterRtlScript" /> (copy-on-write, so
///     lookups stay lock-free).
/// </summary>
public static class TextDirectionInfo
{
    private static readonly object RegisterLock = new();

    // ISO 639 language codes whose default script is right-to-left.
    private static volatile HashSet<string> RtlLanguages = new(StringComparer.OrdinalIgnoreCase) {
        "ar", // Arabic
        "arc", // Aramaic
        "ckb", // Central Kurdish (Sorani) — RTL; the "ku" macrolanguage defaults to Latin, so is NOT listed
        "dv", // Divehi / Maldivian
        "fa", // Persian / Farsi
        "he", // Hebrew
        "iw", // Hebrew (legacy code)
        "khw", // Khowar
        "ks", // Kashmiri
        "ps", // Pashto
        "sd", // Sindhi
        "ug", // Uyghur
        "ur", // Urdu
        "yi", // Yiddish
    };

    // ISO 15924 script codes that are written right-to-left. A script tag is authoritative over the
    // language default — e.g. "az-Arab" is RTL even though Azerbaijani in Latin is LTR.
    private static volatile HashSet<string> RtlScripts = new(StringComparer.OrdinalIgnoreCase) {
        "Arab", // Arabic
        "Aran", // Nastaliq (Arabic variant)
        "Hebr", // Hebrew
        "Syrc", // Syriac
        "Thaa", // Thaana
        "Nkoo", // N'Ko
        "Samr", // Samaritan
        "Mand", // Mandaic
        "Rohg", // Hanifi Rohingya
        "Adlm", // Adlam
    };

    /// <summary>The writing direction for a language + optional script. The script wins when present.</summary>
    public static TextDirection ForLanguage(string? language, string? script = null)
    {
        if (!string.IsNullOrEmpty(script))
            return RtlScripts.Contains(script) ? TextDirection.Rtl : TextDirection.Ltr;
        if (!string.IsNullOrEmpty(language) && RtlLanguages.Contains(language))
            return TextDirection.Rtl;
        return TextDirection.Ltr;
    }

    /// <summary>Mark an ISO 639 language code as right-to-left. Call at startup.</summary>
    public static void RegisterRtlLanguage(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return;
        lock (RegisterLock)
        {
            RtlLanguages = new HashSet<string>(RtlLanguages, StringComparer.OrdinalIgnoreCase) {
                languageCode.Trim(),
            };
        }
    }

    /// <summary>Mark an ISO 15924 script code as right-to-left. Call at startup.</summary>
    public static void RegisterRtlScript(string scriptCode)
    {
        if (string.IsNullOrWhiteSpace(scriptCode)) return;
        lock (RegisterLock)
        {
            RtlScripts = new HashSet<string>(RtlScripts, StringComparer.OrdinalIgnoreCase) {
                scriptCode.Trim(),
            };
        }
    }
}