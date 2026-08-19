using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace Zigote.Http;

/// <summary>
///     Retry with exponential backoff and jitter, plus a per-host circuit breaker. Sits above auth
///     so every attempt gets a valid token, and below dedup so followers wait for the retries rather
///     than each running their own.
/// </summary>
/// <remarks>
///     A request is retried only when all three hold: the verb is idempotent (or the caller said
///     <see cref="RequestPolicy.Idempotent" />), the body is replayable, and the failure is
///     transient. The type system carries the second one — <see cref="HttpBody.IsReplayable" /> —
///     so this layer never has to guess whether a stream can be rewound.
/// </remarks>
public static class RetryMiddleware
{
    /// <summary>The layer.</summary>
    /// <param name="time">Drives the backoff delay and the breaker's clock.</param>
    /// <param name="baseAddress">Resolves relative routes, so the breaker keys by host rather than by route.</param>
    /// <param name="breakerThreshold">Consecutive failures against one host before the circuit opens.</param>
    /// <param name="breakerCooldown">How long the circuit stays open before one probe is let through.</param>
    public static Middleware Create(
        TimeProvider time,
        Uri? baseAddress = null,
        int breakerThreshold = 5,
        TimeSpan breakerCooldown = default)
    {
        var cooldown = breakerCooldown == default ? TimeSpan.FromSeconds(30) : breakerCooldown;
        var breakers = new ConcurrentDictionary<string, Breaker>(StringComparer.OrdinalIgnoreCase);
        // Seeded per pipeline, not per request: jitter only has to decorrelate clients, and a shared
        // Random behind a lock would be the one contended thing in an otherwise lock-free layer.
        var random = new Random();

        return next => async (spec, ct) =>
        {
            var retry = spec.Policy.EffectiveRetry;
            bool eligible = (spec.Verb.IsIdempotent() || spec.Policy.IsIdempotent) &&
                            spec.Body.IsReplayable &&
                            !spec.Policy.IsStreaming &&
                            retry.MaxAttempts > 1;

            string host = HostOf(spec, baseAddress);
            var breaker = breakers.GetOrAdd(host, static _ => new Breaker());

            if (!breaker.Allow(time.GetUtcNow(), cooldown))
            {
                ObservabilityMiddleware.BreakerOpens.Add(1,
                    new KeyValuePair<string, object?>("server.address", host));
                return new HttpError.Policy(PolicyFault.CircuitOpen);
            }

            int attempts = eligible ? retry.MaxAttempts : 1;
            // An origin that says "come back in N seconds" knows better than our backoff curve —
            // but not better than the caller's deadline, which is enforced above us either way.
            TimeSpan? retryAfterHint = null;

            for (int attempt = 1;; attempt++)
            {
                if (attempt > 1)
                {
                    var delay = retryAfterHint ?? retry.Backoff(attempt, random);
                    retryAfterHint = null;
                    ObservabilityMiddleware.Retries.Add(1,
                        new KeyValuePair<string, object?>("url.template", spec.Path.Template));
                    try
                    {
                        await Task.Delay(delay, time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return new HttpError.Canceled();
                    }
                }

                var result = await next(spec, ct).ConfigureAwait(false);
                bool last = attempt >= attempts;

                if (result.IsOk)
                {
                    var response = result.Value;
                    if (!last && HttpError.Status.IsTransientCode(response.Status))
                    {
                        breaker.OnFailure(time.GetUtcNow(), breakerThreshold);
                        // The failed body is not the answer; free it before the next attempt.
                        retryAfterHint = ParseRetryAfter(response.Header("Retry-After"));
                        response.Dispose();
                        continue;
                    }

                    // Any answer from the origin — including a 500 the caller asked us to stop
                    // retrying — proves the host is reachable, which is what the breaker tracks.
                    breaker.OnSuccess();
                    return result;
                }

                if (last || result.Error is not { IsTransient: true } || ct.IsCancellationRequested)
                {
                    if (result.Error is HttpError.Transport or HttpError.Timeout)
                        breaker.OnFailure(time.GetUtcNow(), breakerThreshold);
                    return result;
                }

                breaker.OnFailure(time.GetUtcNow(), breakerThreshold);
            }
        };
    }


    private static TimeSpan? ParseRetryAfter(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
            return seconds is >= 0 and <= 3600 ? TimeSpan.FromSeconds(seconds) : null;
        return null; // the HTTP-date form is rare and needs our clock to agree with theirs; skip it
    }

    private static string HostOf(HttpSpec spec, Uri? baseAddress)
    {
        // Keyed by host, not by route: one origin failing is one circuit, however many endpoints
        // the app talks to on it. ResolveHost is the no-allocation path — for a relative route it
        // is just the base address's host.
        try
        {
            return spec.ResolveHost(baseAddress);
        }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException)
        {
            return spec.Path.Template;
        }
    }

    /// <summary>
    ///     Consecutive-failure breaker for one host. Closed → open after
    ///     <c>threshold</c> failures, open → one probe after the cooldown, and any answer at all
    ///     closes it again.
    /// </summary>
    private sealed class Breaker
    {
        private readonly Lock _lock = new();
        private int _failures;
        private DateTimeOffset _openedAt;
        private bool _probing;

        public bool Allow(DateTimeOffset now, TimeSpan cooldown)
        {
            lock (_lock)
            {
                if (_openedAt == default) return true;
                if (now - _openedAt < cooldown) return false;
                if (_probing) return false;
                _probing = true; // exactly one request per cooldown gets to find out
                return true;
            }
        }

        public void OnSuccess()
        {
            lock (_lock)
            {
                _failures = 0;
                _openedAt = default;
                _probing = false;
            }
        }

        public void OnFailure(DateTimeOffset now, int threshold)
        {
            lock (_lock)
            {
                _probing = false;
                // A failed probe re-opens the circuit for another full cooldown, rather than
                // letting every subsequent request through because the clock already passed.
                if (_openedAt != default) _openedAt = now;
                else if (++_failures >= threshold) _openedAt = now;
            }
        }
    }
}
