using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Zigote.Http;

/// <summary>
///     One span and one set of counters per logical call. Sits just inside the deadline so the span
///     covers everything the budget covers, including the wait for a deduped in-flight request.
/// </summary>
/// <remarks>
///     The span is named by the route <i>template</i>, never the rendered path: <c>assets/{id}</c>
///     groups, <c>assets/8fa1…</c> is a cardinality bomb that a metrics backend charges for.
///     Query strings and <c>Authorization</c> never reach a tag unless
///     <see cref="HttpRunnerOptions.EnableSensitiveLogging" /> is set.
/// </remarks>
public static class ObservabilityMiddleware
{
    /// <summary>The source to subscribe to with an <see cref="ActivityListener" /> or OpenTelemetry.</summary>
    public static readonly ActivitySource Source = new("Zigote.Http", "1.0.0");

    /// <summary>The meter carrying the counters and the duration histogram.</summary>
    public static readonly Meter Meter = new("Zigote.Http", "1.0.0");

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("zigote.http.requests");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("zigote.http.failures");

    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("zigote.http.duration", "ms");

    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>("zigote.http.retries");
    internal static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("zigote.http.cache.hit");
    internal static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("zigote.http.cache.miss");

    internal static readonly Counter<long> CacheRevalidations =
        Meter.CreateCounter<long>("zigote.http.cache.revalidate");

    internal static readonly Counter<long> BreakerOpens = Meter.CreateCounter<long>("zigote.http.breaker.open");

    /// <summary>The layer.</summary>
    /// <param name="time">Drives the duration histogram.</param>
    /// <param name="sensitiveLogging">
    ///     When set, the span also carries the rendered path and query string. Off by default: the
    ///     always-present tag is the route <i>template</i>, which groups without leaking, and a
    ///     query string is where tokens end up. <c>Authorization</c> never reaches a tag either way.
    /// </param>
    public static Middleware Create(TimeProvider time, bool sensitiveLogging = false) => next => async (spec, ct) =>
    {
        string route = spec.Path.Template;
        // HasListeners first: with nobody tracing, this skips the span name's string interpolation
        // too, not just the Activity — the common case in a shipped app costs a branch.
        using var activity = Source.HasListeners()
            ? Source.StartActivity($"{spec.Verb.Token()} {route}", ActivityKind.Client)
            : null;
        activity?.SetTag("http.request.method", spec.Verb.Token());
        activity?.SetTag("url.template", route);

        if (sensitiveLogging && activity is not null)
        {
            // Best effort: a hand-built spec with an unbound placeholder still deserves its span.
            try
            {
                activity.SetTag("url.path", spec.Path.Render());
            }
            catch (InvalidOperationException)
            {
            }

            if (!spec.Query.IsDefaultOrEmpty)
            {
                var query = new System.Text.StringBuilder();
                foreach (var q in spec.Query)
                    query.Append(query.Length > 0 ? "&" : "").Append(q.Name).Append('=').Append(q.Value);
                activity.SetTag("url.query", query.ToString());
            }
        }

        long start = time.GetTimestamp();
        var result = await next(spec, ct).ConfigureAwait(false);
        double ms = time.GetElapsedTime(start).TotalMilliseconds;

        if (Requests.Enabled || Duration.Enabled)
        {
            var verbTag = new KeyValuePair<string, object?>("http.request.method", spec.Verb.Token());
            var routeTag = new KeyValuePair<string, object?>("url.template", route);
            Requests.Add(1, verbTag, routeTag);
            Duration.Record(ms, verbTag, routeTag);
        }

        if (result.IsOk)
        {
            activity?.SetTag("http.response.status_code", (int)result.Value.Status);
            activity?.SetTag("http.response.from_cache", result.Value.FromCache);
            if (!result.Value.IsSuccess) activity?.SetStatus(ActivityStatusCode.Error);
        }
        else
        {
            if (Failures.Enabled)
                Failures.Add(1,
                    new KeyValuePair<string, object?>("http.request.method", spec.Verb.Token()),
                    new KeyValuePair<string, object?>("url.template", route),
                    new KeyValuePair<string, object?>("error.type", result.Error.GetType().Name));
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
        }

        return result;
    };
}
