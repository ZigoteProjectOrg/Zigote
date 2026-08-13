namespace Zigote.UI.Localizations;

/// <summary>
///     Chooses the best supported locale for a user's ordered preferences, following the spirit of
///     RFC 4647 lookup.
///     <para>
///         For each preference, in order, it looks for progressively looser matches — exact →
///         language+region → language+script → any locale in the same language — before moving on to
///         the next preference. If nothing matches, the explicit <c>fallback</c> (or the first
///         supported locale) is returned.
///     </para>
/// </summary>
public static class LocaleResolution
{
    /// <summary>Resolve a single preferred locale against the supported set.</summary>
    public static Locale
        Resolve(Locale preferred, IReadOnlyList<Locale> supported, Locale fallback) => Resolve(
        preferred: new[] { preferred },
        supported: supported,
        fallback: fallback
    );

    /// <summary>
    ///     Resolve an ordered preference list against the supported set. Earlier preferences win over
    ///     later ones even when a later one would match more tightly.
    /// </summary>
    public static Locale Resolve(
        IReadOnlyList<Locale> preferred,
        IReadOnlyList<Locale> supported,
        Locale fallback)
    {
        if (supported.Count == 0)
            return fallback.IsEmpty ? DefaultOf(preferred) : fallback;

        foreach (var want in preferred)
        {
            if (want.IsEmpty) continue;

            // 1. Exact match (language + script + region).
            if (Contains(supported: supported, candidate: want, match: out var exact)) return exact;

            // 2. When a script is requested, honour it before dropping to a region match — the writing
            //    system matters more than the region. Prefer a same-language + same-script entry (any
            //    region) so e.g. zh-Hant-CN picks zh-Hant-TW over zh-CN.
            if (want.Script is not null)
            {
                foreach (var s in supported)
                {
                    if (string.Equals(
                            a: s.Language,
                            b: want.Language,
                            comparisonType: StringComparison.Ordinal
                        )
                        && string.Equals(
                            a: s.Script,
                            b: want.Script,
                            comparisonType: StringComparison.Ordinal
                        ))
                        return s;
                }
            }

            // 3. Language + region, ignoring script differences.
            var langRegion = want.WithoutScript();
            if (langRegion != want && Contains(
                    supported: supported,
                    candidate: langRegion,
                    match: out var lr
                )) return lr;

            // 4. Language-only exact entry.
            var langOnly = want.LanguageOnly();
            if (Contains(supported: supported, candidate: langOnly, match: out var lo)) return lo;

            // 5. Any supported locale in the same language (prefer a script match, then first).
            Locale? sameLangScript = null;
            Locale? sameLang = null;
            foreach (var s in supported)
            {
                if (!string.Equals(
                        a: s.Language,
                        b: want.Language,
                        comparisonType: StringComparison.Ordinal
                    )) continue;
                sameLang ??= s;
                if (want.Script is not null && string.Equals(
                        a: s.Script,
                        b: want.Script,
                        comparisonType: StringComparison.Ordinal
                    ))
                {
                    sameLangScript = s;
                    break;
                }
            }

            if (sameLangScript is { } sls) return sls;
            if (sameLang is { } sl) return sl;
        }

        // Nothing matched any preference.
        if (!fallback.IsEmpty && supported.Contains(fallback)) return fallback;
        return supported[0];
    }

    private static bool Contains(IReadOnlyList<Locale> supported, Locale candidate,
        out Locale match)
    {
        foreach (var s in supported)
        {
            if (s == candidate)
            {
                match = s;
                return true;
            }
        }

        match = default;
        return false;
    }

    private static Locale DefaultOf(IReadOnlyList<Locale> preferred)
    {
        foreach (var p in preferred)
        {
            if (!p.IsEmpty)
                return p;
        }

        return Locale.En;
    }
}
