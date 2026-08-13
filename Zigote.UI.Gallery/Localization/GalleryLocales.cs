using Zigote.UI.Host;
using Zigote.UI.Localizations;

namespace Gallery;

/// <summary>
///     Per-locale font selection. The engine renders each text run with a single face (no
///     per-script fallback yet), and the bundled Inter covers Latin + Cyrillic only — so for
///     CJK/Arabic locales the default UI family is re-registered onto a pan-Unicode system face
///     (macOS ships Arial Unicode). <see cref="App.SetFontFace" /> swaps the face under the same
///     family name and drops every text cache, so the whole UI re-shapes at once.
/// </summary>
internal static class GalleryFonts
{
    private const string PanUnicodeFace = "/System/Library/Fonts/Supplemental/Arial Unicode.ttf";
    private static string? _current;
    private static bool _intlRegistered;

    /// <summary>
    ///     The pan-Unicode face registered as its own family, or null when unavailable. Used by
    ///     widgets that must render scripts the ACTIVE face may not cover — the language switcher
    ///     shows every locale's native name (中文/العربية/…) while the UI is still on Inter.
    /// </summary>
    public static string? PanUnicodeFamily => _intlRegistered ? "intl" : null;

    /// <summary>Register the pan-Unicode face under the "intl" family (once, at boot).</summary>
    public static void RegisterPanUnicodeFamily(App? app)
    {
        if (app is null || _intlRegistered || !File.Exists(PanUnicodeFace)) return;
        _intlRegistered = app.SetFontFace("intl", PanUnicodeFace);
    }

    public static void Apply(App? app, Locale locale)
    {
        if (app is null) return;

        var needsPanUnicode = locale.Language is "zh" or "ja" or "ar";
        var inter = Path.Combine(
            AppContext.BaseDirectory,
            "Fonts",
            "Inter-Regular.ttf"
        );
        var path = needsPanUnicode && File.Exists(PanUnicodeFace) ? PanUnicodeFace : inter;

        if (path == _current || !File.Exists(path)) return;
        if (app.SetFontFace("Inter", path)) _current = path;
    }
}
