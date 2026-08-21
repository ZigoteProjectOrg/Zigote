using System.Collections.Concurrent;

namespace Zigote.Http;

/// <summary>
///     A per-host gate: at most N requests in flight against one origin, the rest queueing on an
///     awaited semaphore (a queued request costs a continuation, never a thread). Browsers settle
///     around six per host for the same reason — past that, a wider fan-out trades latency for
///     nothing, and an image grid submitting a thousand tiles becomes a self-inflicted DoS.
/// </summary>
/// <remarks>
///     Sits below dedup — collapsed requests never occupy a slot — and above retry, so a queued
///     request has not started burning attempts. The wait happens <i>inside</i> the caller's
///     deadline, on purpose: "finish within 5 s" has to mean queue time too, or the deadline is a
///     lie under load. Off unless <see cref="HttpRunnerOptions.MaxConcurrencyPerHost" /> is set —
///     adding an invisible queue to every existing runner would change timing behaviour silently.
/// </remarks>
public static class ConcurrencyMiddleware
{
    /// <summary>The layer. <paramref name="maxPerHost" /> must be at least 1.</summary>
    /// <param name="maxPerHost">Concurrent requests allowed per host.</param>
    /// <param name="baseAddress">Resolves relative routes, so the gate keys by host.</param>
    public static Middleware Create(int maxPerHost, Uri? baseAddress)
    {
        var gates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        return next => async (spec, ct) =>
        {
            string host;
            try
            {
                host = spec.ResolveHost(baseAddress); // no Uri parse for a relative route
            }
            catch (InvalidOperationException)
            {
                // An unresolvable spec fails identically a few layers down; gating it here would
                // just report the wrong error from the wrong place.
                return await next(spec, ct).ConfigureAwait(false);
            }

            // Static lambda + factory arg: the capturing overload allocates a closure per request
            // even on the (always) cache-hit path. Same pattern as RetryMiddleware's breakers.
            var gate = gates.GetOrAdd(
                key: host,
                valueFactory: static (_, max) => new SemaphoreSlim(initialCount: max, maxCount: max),
                factoryArgument: maxPerHost
            );
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new HttpError.Canceled(); // named as Timeout(Total) above us if it was the deadline
            }

            try
            {
                return await next(spec, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        };
    }
}
