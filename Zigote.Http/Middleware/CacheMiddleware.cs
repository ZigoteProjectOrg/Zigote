using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using Zigote.Http.Cache;

namespace Zigote.Http;

/// <summary>
///     The RFC 9111 subset described in the design doc: freshness, conditional revalidation,
///     <c>stale-while-revalidate</c>, and five explicit per-request modes. Coverage was traded for
///     predictability — heuristic freshness is off unless asked for, and one variant is stored per
///     key.
/// </summary>
public static class CacheMiddleware
{
    /// <summary>The layer.</summary>
    /// <param name="store">Where entries live.</param>
    /// <param name="baseAddress">Resolves relative routes into the absolute URI that keys an entry.</param>
    /// <param name="time">Clock for freshness. Injected, so every expiry test is deterministic.</param>
    /// <param name="lifetime">
    ///     The runner's lifetime. Background <c>stale-while-revalidate</c> refreshes are deliberately
    ///     untied from any caller — but not from the runner: disposing it must not leave requests
    ///     running against a transport that is being torn down.
    /// </param>
    public static Middleware Create(
        IHttpCacheStore store, Uri? baseAddress, TimeProvider time, CancellationToken lifetime = default)
    {
        // Guards against a burst of stale-while-revalidate hits firing one refresh each.
        var refreshing = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        return next => async (spec, ct) =>
        {
            var mode = spec.Policy.EffectiveCache;
            if (mode == CacheMode.Bypass || spec.Policy.IsStreaming || !spec.Verb.IsCacheable())
            {
                var passed = await next(spec, ct).ConfigureAwait(false);

                // RFC 9111 §4.4: a non-error answer to an unsafe method proves the resource changed,
                // so the stored GET/HEAD entries for that URI are now lies. Without this, PUTting an
                // asset and GETting it back serves the old copy until it expires — the worst kind of
                // bug an editor can have.
                if (passed.IsOk && (int)passed.Value.Status < 400 &&
                    spec.Verb is HttpVerb.Post or HttpVerb.Put or HttpVerb.Patch or HttpVerb.Delete)
                {
                    // Rebuilt through the same key builder the read path uses, so the strings
                    // cannot drift apart. The verb prefix is what changes.
                    string written = spec.CacheKey(baseAddress);
                    string target = written[(spec.Verb.Token().Length + 1)..];
                    await store.RemoveAsync($"GET {target}", ct).ConfigureAwait(false);
                    await store.RemoveAsync($"HEAD {target}", ct).ConfigureAwait(false);
                }

                return passed;
            }

            string key = spec.CacheKey(baseAddress);
            var entry = mode == CacheMode.RefreshOnly
                ? null
                : await store.GetAsync(key, ct).ConfigureAwait(false);

            if (entry is not null && !VaryMatches(entry, spec))
            {
                // A different variant is stored. Treat as a miss; the store call below replaces it.
                entry = null;
            }

            var routeTag = new KeyValuePair<string, object?>("url.template", spec.Path.Template);
            if (entry is not null)
            {
                switch (FreshnessRules.Evaluate(entry, spec.Policy, time.GetUtcNow()))
                {
                    case Freshness.Fresh:
                        ObservabilityMiddleware.CacheHits.Add(1, routeTag);
                        return entry.ToResponse();

                    case Freshness.StaleUsable:
                        ObservabilityMiddleware.CacheHits.Add(1, routeTag);
                        StartBackgroundRefresh(next, store, spec, key, time, refreshing, lifetime);
                        return entry.ToResponse();
                }
            }

            if (mode == CacheMode.CacheOnly)
            {
                ObservabilityMiddleware.CacheMisses.Add(1, routeTag);
                return new HttpError.Policy(PolicyFault.CacheMiss);
            }

            var request = entry is null ? spec : WithValidators(spec, entry);
            if (entry is not null) ObservabilityMiddleware.CacheRevalidations.Add(1, routeTag);
            else ObservabilityMiddleware.CacheMisses.Add(1, routeTag);

            var result = await next(request, ct).ConfigureAwait(false);
            if (!result.IsOk) return result;

            var response = result.Value;

            if (response.Status == HttpStatusCode.NotModified && entry is not null)
            {
                // §4.3.4: the stored headers are updated from the 304 and the stored body is reused.
                response.Dispose();
                var refreshed = entry with
                {
                    Headers = MergeHeaders(entry.Headers, response.Headers),
                    StoredAt = time.GetUtcNow(),
                    InitialAgeSeconds = 0
                };
                await store.SetAsync(key, refreshed, ct).ConfigureAwait(false);
                return refreshed.ToResponse();
            }

            if (IsStorable(spec, response))
            {
                var stored = ToEntry(response, spec, time.GetUtcNow());
                await store.SetAsync(key, stored, ct).ConfigureAwait(false);
                // Hand back the stored copy: it owns a plain array, so the pooled buffer the
                // transport rented goes back to the pool here rather than in the caller's hands.
                response.Dispose();
                return HttpResponse.FromBytes(stored.Status, stored.Headers, stored.Body);
            }

            if (!spec.Verb.IsCacheable() || !FreshnessRules.IsStorableStatus((int)response.Status))
                return response;

            // Explicitly uncacheable (no-store, or private auth without public): drop any stale copy
            // rather than leaving one that would be served later.
            if (entry is not null) await store.RemoveAsync(key, ct).ConfigureAwait(false);
            return response;
        };
    }

    private static void StartBackgroundRefresh(
        Send next, IHttpCacheStore store, HttpSpec spec, string key, TimeProvider time,
        ConcurrentDictionary<string, byte> refreshing, CancellationToken lifetime)
    {
        if (!refreshing.TryAdd(key, 0)) return;

        // Untied to the caller's token on purpose: the caller already has an answer, and a refresh
        // that dies because one widget navigated away is a refresh that never happens. Tied to the
        // runner's lifetime instead — dispose is the one caller allowed to stop it.
        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await next(spec with { Policy = spec.Policy with { Cache = CacheMode.Bypass } },
                    lifetime).ConfigureAwait(false);
                if (fresh.IsOk)
                {
                    using var response = fresh.Value;
                    if (IsStorable(spec, response))
                        await store.SetAsync(key, ToEntry(response, spec, time.GetUtcNow())).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
            {
                // A failed background refresh leaves the stale entry in place. That is the point.
            }
            finally
            {
                refreshing.TryRemove(key, out _);
            }
        });
    }

    private static HttpSpec WithValidators(HttpSpec spec, CachedResponse entry)
    {
        var headers = spec.Headers.IsDefault ? ImmutableArray<HeaderPair>.Empty : spec.Headers;
        if (entry.Header("ETag") is { Length: > 0 } etag)
            headers = headers.Add(new HeaderPair("If-None-Match", etag));
        else if (entry.Header("Last-Modified") is { Length: > 0 } modified)
            headers = headers.Add(new HeaderPair("If-Modified-Since", modified));
        else
            return spec; // nothing to revalidate with: a plain request is the honest fallback

        return spec with { Headers = headers };
    }

    private static bool IsStorable(HttpSpec spec, HttpResponse response)
    {
        if (!spec.Verb.IsCacheable() || !FreshnessRules.IsStorableStatus((int)response.Status)) return false;
        if (response.ContentStream is not null) return false;

        var directives = CacheDirectives.Parse(response.Header("Cache-Control"));
        if (directives.NoStore) return false;

        // A response to an authorized request is stored only when the origin says it may be
        // (§3.5). Getting this wrong is how one user's data ends up in another user's editor.
        bool authorized = false;
        if (!spec.Headers.IsDefaultOrEmpty)
            foreach (var h in spec.Headers)
                if (string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase))
                    authorized = true;

        return !authorized || directives.Public || directives.SharedMaxAge >= 0;
    }

    private static CachedResponse ToEntry(HttpResponse response, HttpSpec spec, DateTimeOffset now) =>
        new(response.Status,
            response.Headers,
            response.Body.ToArray(),
            now,
            long.TryParse(response.Header("Age"), out long age) ? age : 0,
            VaryKey(response.Headers, spec));

    private static ImmutableArray<HeaderPair> MergeHeaders(
        ImmutableArray<HeaderPair> stored, ImmutableArray<HeaderPair> received)
    {
        if (received.IsDefaultOrEmpty) return stored;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in received) names.Add(h.Name);

        var merged = ImmutableArray.CreateBuilder<HeaderPair>(stored.Length + received.Length);
        foreach (var h in stored)
            if (!names.Contains(h.Name))
                merged.Add(h);
        merged.AddRange(received);
        return merged.ToImmutable();
    }

    private static bool VaryMatches(CachedResponse entry, HttpSpec spec) =>
        // "Vary: *" is stored as an unmatchable key, so it never matches — including itself.
        entry.VaryKey != "*" &&
        string.Equals(entry.VaryKey, VaryKey(entry.Headers, spec), StringComparison.Ordinal);

    private static string VaryKey(ImmutableArray<HeaderPair> responseHeaders, HttpSpec spec)
    {
        string? vary = null;
        foreach (var h in responseHeaders)
            if (string.Equals(h.Name, "Vary", StringComparison.OrdinalIgnoreCase))
                vary = vary is null ? h.Value : $"{vary},{h.Value}";

        if (string.IsNullOrEmpty(vary)) return string.Empty;
        // "Vary: *" means no stored response may be reused. An unmatchable key says exactly that.
        if (vary.Contains('*')) return "*";

        var sb = new StringBuilder();
        foreach (var range in vary.AsSpan().Split(','))
        {
            var name = vary.AsSpan()[range].Trim();
            if (name.IsEmpty) continue;
            sb.Append(name).Append('=');
            if (!spec.Headers.IsDefaultOrEmpty)
                foreach (var h in spec.Headers)
                    if (name.Equals(h.Name, StringComparison.OrdinalIgnoreCase))
                        sb.Append(h.Value).Append(';');
            sb.Append('|');
        }

        return sb.ToString();
    }
}
