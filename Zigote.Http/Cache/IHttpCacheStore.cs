using System.Collections.Immutable;
using System.Net;

namespace Zigote.Http.Cache;

/// <summary>
///     A stored response. Immutable apart from <see cref="Headers" /> being replaced wholesale on a
///     304, which is exactly what RFC 9111 §4.3.4 asks for.
/// </summary>
/// <param name="Status">The status that was stored.</param>
/// <param name="Headers">The stored response headers.</param>
/// <param name="Body">The stored body. Never pooled — a store outlives any one request.</param>
/// <param name="StoredAt">When this entry was written, from the injected <see cref="TimeProvider" />.</param>
/// <param name="InitialAgeSeconds">The <c>Age</c> the response already carried when it arrived.</param>
/// <param name="VaryKey">
///     The request headers named by the response's <c>Vary</c>, joined — empty when the origin
///     varies on nothing. Compared on lookup: a mismatch is a miss, and the entry is replaced.
///     One variant per key, which is the predictable half of RFC 9111 §4.1 and covers every origin
///     that varies on <c>Accept</c> or <c>Accept-Language</c>.
/// </param>
public sealed record CachedResponse(
    HttpStatusCode Status,
    ImmutableArray<HeaderPair> Headers,
    byte[] Body,
    DateTimeOffset StoredAt,
    long InitialAgeSeconds,
    string VaryKey)
{
    /// <summary>The first value of <paramref name="name" />, or null.</summary>
    public string? Header(string name)
    {
        foreach (var h in Headers)
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                return h.Value;
        return null;
    }

    /// <summary>This entry as a response, flagged as a cache hit and owning nothing poolable.</summary>
    public HttpResponse ToResponse() => HttpResponse.FromBytes(Status, Headers, Body, fromCache: true);
}

/// <summary>
///     Where cached responses live. Two implementations ship — <see cref="MemoryCacheStore" /> and
///     <see cref="FileCacheStore" /> — and an app that wants SQLite or the Zigote asset database
///     implements this instead.
/// </summary>
/// <remarks>
///     Implementations must be safe for concurrent use, and must treat a corrupt or unreadable entry
///     as a miss rather than an error: a cache that can fail a request is worse than no cache.
/// </remarks>
public interface IHttpCacheStore
{
    /// <summary>The entry under <paramref name="key" />, or null.</summary>
    ValueTask<CachedResponse?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Store or replace the entry under <paramref name="key" />.</summary>
    ValueTask SetAsync(string key, CachedResponse entry, CancellationToken ct = default);

    /// <summary>Drop the entry under <paramref name="key" /> if it exists.</summary>
    ValueTask RemoveAsync(string key, CancellationToken ct = default);
}
