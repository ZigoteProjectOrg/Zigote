using System.Globalization;

namespace Zigote.Http.Cache;

/// <summary>The <c>Cache-Control</c> directives this library acts on. Everything else is ignored, on purpose.</summary>
public readonly record struct CacheDirectives
{
    /// <summary>Nothing was said.</summary>
    public static CacheDirectives None => default;

    /// <summary><c>no-store</c>: never write this to a cache.</summary>
    public bool NoStore { get; private init; }

    /// <summary><c>no-cache</c>: storable, but must be revalidated before every reuse.</summary>
    public bool NoCache { get; private init; }

    /// <summary><c>private</c>: not storable by a shared cache. Ours is per-user, so this is informational.</summary>
    public bool Private { get; private init; }

    /// <summary><c>public</c>: storable even with an <c>Authorization</c> header.</summary>
    public bool Public { get; private init; }

    /// <summary><c>immutable</c>: never revalidate while fresh, even on an explicit reload.</summary>
    public bool Immutable { get; private init; }

    /// <summary><c>max-age</c>, or -1.</summary>
    public long MaxAge { get; private init; }

    /// <summary><c>s-maxage</c>, or -1. Wins over <c>max-age</c> where present.</summary>
    public long SharedMaxAge { get; private init; }

    /// <summary><c>stale-while-revalidate</c>, or 0.</summary>
    public long StaleWhileRevalidate { get; private init; }

    /// <summary>Parse one <c>Cache-Control</c> field value. Unknown directives are skipped.</summary>
    public static CacheDirectives Parse(string? value)
    {
        var result = new CacheDirectives { MaxAge = -1, SharedMaxAge = -1 };
        if (string.IsNullOrEmpty(value)) return result;

        foreach (var range in value.AsSpan().Split(','))
        {
            var token = value.AsSpan()[range].Trim();
            int eq = token.IndexOf('=');
            var name = eq < 0 ? token : token[..eq].Trim();
            var arg = eq < 0 ? default : token[(eq + 1)..].Trim().Trim('"');

            if (name.Equals("no-store", StringComparison.OrdinalIgnoreCase)) result = result with { NoStore = true };
            else if (name.Equals("no-cache", StringComparison.OrdinalIgnoreCase)) result = result with { NoCache = true };
            else if (name.Equals("private", StringComparison.OrdinalIgnoreCase)) result = result with { Private = true };
            else if (name.Equals("public", StringComparison.OrdinalIgnoreCase)) result = result with { Public = true };
            else if (name.Equals("immutable", StringComparison.OrdinalIgnoreCase)) result = result with { Immutable = true };
            else if (name.Equals("max-age", StringComparison.OrdinalIgnoreCase) && Seconds(arg) is var a and >= 0)
                result = result with { MaxAge = a };
            else if (name.Equals("s-maxage", StringComparison.OrdinalIgnoreCase) && Seconds(arg) is var s and >= 0)
                result = result with { SharedMaxAge = s };
            else if (name.Equals("stale-while-revalidate", StringComparison.OrdinalIgnoreCase) &&
                     Seconds(arg) is var w and >= 0)
                result = result with { StaleWhileRevalidate = w };
        }

        return result;

        static long Seconds(ReadOnlySpan<char> span) =>
            long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : -1;
    }
}

/// <summary>How usable a stored entry is right now.</summary>
public enum Freshness
{
    /// <summary>Serve it. No network.</summary>
    Fresh,

    /// <summary>Serve it now and refresh in the background (<c>stale-while-revalidate</c>).</summary>
    StaleUsable,

    /// <summary>Ask the origin with <c>If-None-Match</c> / <c>If-Modified-Since</c> before serving.</summary>
    MustRevalidate
}

/// <summary>The RFC 9111 freshness subset this library implements. Pure functions over an injected clock.</summary>
public static class FreshnessRules
{
    /// <summary>
    ///     Statuses worth storing. Deliberately short: the two-hundreds people actually cache, the
    ///     permanent redirects, and the two negative answers that save a round trip
    ///     (404/410) — RFC 9111 §3 calls these heuristically cacheable and they are the ones that
    ///     matter for an editor walking an asset tree.
    /// </summary>
    public static bool IsStorableStatus(int status) =>
        status is 200 or 203 or 300 or 301 or 308 or 404 or 410;

    /// <summary>How old the entry is, counting the <c>Age</c> it arrived with.</summary>
    public static TimeSpan Age(CachedResponse entry, DateTimeOffset now) =>
        TimeSpan.FromSeconds(entry.InitialAgeSeconds) + (now - entry.StoredAt);

    /// <summary>
    ///     How long the entry may be served without asking. <c>s-maxage</c> beats <c>max-age</c>
    ///     beats <c>Expires - Date</c>; with none of those, heuristic freshness is used only when
    ///     <paramref name="allowHeuristic" /> says so, and then at the conventional 10% of the
    ///     resource's observed age, capped at a day.
    /// </summary>
    public static TimeSpan Lifetime(CachedResponse entry, CacheDirectives directives, bool allowHeuristic)
    {
        if (directives.SharedMaxAge >= 0) return TimeSpan.FromSeconds(directives.SharedMaxAge);
        if (directives.MaxAge >= 0) return TimeSpan.FromSeconds(directives.MaxAge);

        var date = ParseDate(entry.Header("Date")) ?? entry.StoredAt;
        if (ParseDate(entry.Header("Expires")) is { } expires) return expires - date;

        if (!allowHeuristic) return TimeSpan.Zero;
        if (ParseDate(entry.Header("Last-Modified")) is not { } modified) return TimeSpan.Zero;
        var heuristic = TimeSpan.FromTicks((date - modified).Ticks / 10);
        return heuristic > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : heuristic;
    }

    /// <summary>The verdict for one stored entry under one request's policy.</summary>
    public static Freshness Evaluate(CachedResponse entry, RequestPolicy policy, DateTimeOffset now)
    {
        var directives = CacheDirectives.Parse(entry.Header("Cache-Control"));
        if (directives.NoCache) return Freshness.MustRevalidate;
        if (policy.EffectiveCache == CacheMode.Revalidate && !directives.Immutable) return Freshness.MustRevalidate;

        var age = Age(entry, now);
        var lifetime = Lifetime(entry, directives, policy.HeuristicFreshnessAllowed);
        if (age < lifetime) return Freshness.Fresh;

        // Serve stale and refresh behind the caller's back: the right default for an editor
        // fetching an asset manifest, where a second-old answer now beats a correct answer later.
        return age < lifetime + TimeSpan.FromSeconds(directives.StaleWhileRevalidate)
            ? Freshness.StaleUsable
            : Freshness.MustRevalidate;
    }

    /// <summary>An HTTP-date, or null. Only the IMF-fixdate form origins actually send.</summary>
    public static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                ? parsed
                : null;
}
