using System.Collections.Concurrent;
using System.Text;

namespace Zigote.Http;

/// <summary>
///     Single-flight: N concurrent identical GETs share one trip to the origin. Sits below the
///     cache — a cache hit never gets here — and above retry, so the shared attempt is the one that
///     retries and the followers wait for the outcome rather than each starting their own backoff.
/// </summary>
/// <remarks>
///     A response that ends up shared is <see cref="HttpResponse.Detach">detached</see> first, so
///     the followers hold one immutable body and nobody can double-free a pooled buffer; a flight
///     nobody joined — the overwhelmingly common case — skips that copy entirely. One caller
///     cancelling abandons that caller's wait, never the shared request — which is the behaviour a
///     grid of widgets scrolling past a URL needs.
/// </remarks>
public static class DedupMiddleware
{
    /// <summary>The layer.</summary>
    /// <param name="baseAddress">Resolves relative routes into the key requests are grouped by.</param>
    public static Middleware Create(Uri? baseAddress)
    {
        var inFlight = new ConcurrentDictionary<string, Flight>(StringComparer.Ordinal);

        return next => async (spec, ct) =>
        {
            // Only safe verbs, and only when the body cannot differ: two POSTs to one URL are two
            // different intentions even when they look identical.
            if (!spec.Verb.IsCacheable() || spec.Policy.IsStreaming || spec.Body is not HttpBody.NoBody)
                return await next(spec, ct).ConfigureAwait(false);

            string key = FlightKey(spec, baseAddress);
            var flight = new Flight();
            var shared = inFlight.GetOrAdd(key, flight);

            if (ReferenceEquals(shared, flight))
            {
                HttpResult<HttpResponse> raw;
                try
                {
                    raw = await next(spec, ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    inFlight.TryRemove(new KeyValuePair<string, Flight>(key, flight));
                    flight.Outcome.SetException(e);
                    throw;
                }

                // Removal first: after this, no new follower can see the flight. Anyone who did
                // attach incremented Followers before awaiting, so reading it now tells the truth.
                inFlight.TryRemove(new KeyValuePair<string, Flight>(key, flight));

                if (Volatile.Read(ref flight.Followers) == 0)
                {
                    // The common case: nobody joined, so nothing shares the response and the copy
                    // a shared body would need is skipped entirely. Completing the outcome as
                    // Canceled covers the one racer who grabbed the flight but had not yet
                    // registered — the follower path below re-runs on that answer.
                    flight.Outcome.SetResult(new HttpError.Canceled());
                    return raw;
                }

                // Shared: hand every waiter one immutable copy nothing can double-free.
                var owned = raw.IsOk ? raw.Value.Detach() : raw;
                flight.Outcome.SetResult(owned);
                return owned;
            }

            Interlocked.Increment(ref shared.Followers);

            HttpResult<HttpResponse> result;
            try
            {
                // WaitAsync gives each follower its own cancellation without touching the work.
                result = await shared.Outcome.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new HttpError.Canceled();
            }

            // Two ways to land here with Canceled and a live token: the leader's caller walked
            // away, or this follower lost the registration race above. Either way the request is
            // still wanted, so run it — the leader owned the socket, not the intention.
            if (result.Error is HttpError.Canceled && !ct.IsCancellationRequested)
                return await next(spec, ct).ConfigureAwait(false);

            return result;
        };
    }

    /// <summary>One in-flight GET: who is waiting, and what it resolved to.</summary>
    private sealed class Flight
    {
        public readonly TaskCompletionSource<HttpResult<HttpResponse>> Outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Followers;
    }

    /// <summary>
    ///     What makes two in-flight requests the same request. Headers are part of it, not
    ///     decoration: two GETs to one URL with different <c>Range</c> headers are two different
    ///     answers, and collapsing them would hand a caller the wrong bytes.
    /// </summary>
    private static string FlightKey(HttpSpec spec, Uri? baseAddress)
    {
        string key = spec.CacheKey(baseAddress);
        if (spec.Headers.IsDefaultOrEmpty) return key;

        var sb = new StringBuilder(key);
        foreach (var h in spec.Headers) sb.Append('\n').Append(h.Name).Append(':').Append(h.Value);
        return sb.ToString();
    }
}
