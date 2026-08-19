using System.Collections.Immutable;
using System.Net;

namespace Zigote.Http;

/// <summary>
///     Where a token comes from. One method, because "give me a token" and "that one was rejected,
///     give me a new one" are the same question with one bit of context.
/// </summary>
public interface IHttpAuthProvider
{
    /// <summary>The scheme written into <c>Authorization</c>. Usually <c>Bearer</c>.</summary>
    string Scheme { get; }

    /// <summary>
    ///     The current token, or null to send the request unauthenticated.
    ///     <paramref name="rejectedToken" /> is null when a request is simply asking for a token,
    ///     and carries the exact token a 401 refused otherwise — which is what lets an
    ///     implementation tell "refresh, mine was rejected" from "someone already refreshed and you
    ///     are holding the answer".
    /// </summary>
    ValueTask<string?> GetTokenAsync(string? rejectedToken, CancellationToken ct);
}

/// <summary>
///     A provider over a delegate that fetches a token, with the refresh single-flighted and the
///     result held until something rejects it — or, when <paramref name="refreshAfter" /> is set,
///     until it ages out, so a token with a known lifetime is replaced <i>before</i> a request has
///     to eat a 401 to discover it expired. Covers the common case; an app with a real OAuth
///     lifecycle implements <see cref="IHttpAuthProvider" /> instead.
/// </summary>
/// <param name="fetch">Fetches a fresh token, or null for "send unauthenticated".</param>
/// <param name="scheme">The <c>Authorization</c> scheme.</param>
/// <param name="refreshAfter">
///     Proactive refresh age. Set it below the token's real lifetime (80% is the convention) so
///     refresh happens on a request that would have succeeded anyway, not on one that just failed.
/// </param>
/// <param name="time">Clock for the age check. Injected, so expiry tests are deterministic.</param>
public sealed class TokenAuthProvider(
    Func<CancellationToken, ValueTask<string?>> fetch,
    string scheme = "Bearer",
    TimeSpan? refreshAfter = null,
    TimeProvider? time = null) : IHttpAuthProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _fetchedAt;
    private volatile string? _token;

    /// <inheritdoc />
    public string Scheme { get; } = scheme;

    /// <inheritdoc />
    public async ValueTask<string?> GetTokenAsync(string? rejectedToken, CancellationToken ct)
    {
        // Comparing against the rejected token, rather than counting refreshes, is what makes N
        // concurrent 401s cost exactly one fetch: a caller whose token is no longer the current one
        // is holding a stale complaint, and the answer is already in hand.
        if (Current(rejectedToken) is { } known) return known;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Current(rejectedToken) is { } refreshed) return refreshed;
            _token = await fetch(ct).ConfigureAwait(false);
            Volatile.Write(ref _fetchedAt, _time.GetUtcNow().UtcTicks);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forget the current token, so the next request fetches one.</summary>
    public void Invalidate() => _token = null;

    private string? Current(string? rejectedToken)
    {
        if (_token is not { } token) return null;
        if (string.Equals(token, rejectedToken, StringComparison.Ordinal)) return null;

        // Aged out: report "no token" so the caller path falls into the single-flighted fetch.
        if (refreshAfter is { } maxAge &&
            _time.GetUtcNow().UtcTicks - Volatile.Read(ref _fetchedAt) > maxAge.Ticks)
            return null;

        return token;
    }
}

/// <summary>
///     Attaches the token and replays once on a 401. Sits <i>inside</i> retry so each attempt
///     carries a token that was valid when the attempt was made — a token fetched before a two
///     second backoff is a token that can expire during it.
/// </summary>
public static class AuthMiddleware
{
    /// <summary>The layer.</summary>
    public static Middleware Create(IHttpAuthProvider provider) => next => async (spec, ct) =>
    {
        if (spec.Policy.IsAnonymous) return await next(spec, ct).ConfigureAwait(false);

        string? token = await provider.GetTokenAsync(rejectedToken: null, ct).ConfigureAwait(false);
        var result = await next(WithToken(spec, provider.Scheme, token), ct).ConfigureAwait(false);

        // Replay once, and only when the body survives being sent twice. A one-shot stream is
        // already half on the wire by the time the 401 arrives.
        if (result.Error is not null || result.Value.Status != HttpStatusCode.Unauthorized ||
            !spec.Body.IsReplayable || spec.Policy.IsStreaming)
            return result;

        string? refreshed = await provider.GetTokenAsync(token, ct).ConfigureAwait(false);
        if (refreshed is null || refreshed == token) return result;

        result.Value.Dispose();
        return await next(WithToken(spec, provider.Scheme, refreshed), ct).ConfigureAwait(false);
    };

    private static HttpSpec WithToken(HttpSpec spec, string scheme, string? token)
    {
        if (token is null) return spec;
        var headers = spec.Headers.IsDefault ? ImmutableArray<HeaderPair>.Empty : spec.Headers;
        return spec with { Headers = headers.Add(new HeaderPair("Authorization", $"{scheme} {token}")) };
    }
}
