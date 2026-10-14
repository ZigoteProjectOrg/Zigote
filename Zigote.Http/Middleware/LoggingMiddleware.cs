using System.Net;

namespace Zigote.Http;

/// <summary>
///     One structured record per logical call — what a log line should carry, as data. Handed to
///     the sink the app registered; bridging to any logger is one lambda:
///     <code>OnLog = e =&gt; logger.LogInformation("{Verb} {Route} → {Outcome} in {Ms}ms", e.Verb, e.Route, e.Outcome, e.Elapsed.TotalMilliseconds)</code>
/// </summary>
/// <param name="Verb">The wire verb.</param>
/// <param name="Route">The route template — always safe to index and group by.</param>
/// <param name="Target">
///     The rendered path and query, only when the runner set
///     <see cref="HttpRunnerOptions.EnableSensitiveLogging" />; null otherwise. Redaction is the
///     default because a query string is where tokens and user identifiers end up.
///     <c>Authorization</c> never appears in either field.
/// </param>
/// <param name="Status">The status, when an answer arrived (whatever its code).</param>
/// <param name="Error">The error, when one did not.</param>
/// <param name="Elapsed">Wall time for the whole logical call, queue and retries included.</param>
/// <param name="FromCache">Whether the cache answered without the network.</param>
/// <param name="BodyBytes">Buffered body size; 0 for streaming responses and errors.</param>
public readonly record struct HttpLogEvent(
    string Verb,
    string Route,
    string? Target,
    HttpStatusCode? Status,
    HttpError? Error,
    TimeSpan Elapsed,
    bool FromCache,
    int BodyBytes)
{
    /// <summary>One word for the log line: the status code, or the error's message.</summary>
    public string Outcome => Status is { } status ? $"{(int)status}" : Error?.Message ?? "?";
}

/// <summary>
///     Emits an <see cref="HttpLogEvent" /> per logical call to the app's sink. A sibling of the
///     metrics layer, not a replacement: metrics answer "how often", a log answers "what happened
///     at 14:32". The sink is a delegate rather than an <c>ILogger</c> so the library stays
///     dependency-free; the bridge is one line either way.
/// </summary>
public static class LoggingMiddleware
{
    /// <summary>The layer. The sink runs inline on the request's continuation — keep it cheap.</summary>
    public static Middleware Create(Action<HttpLogEvent> sink, TimeProvider time, bool sensitive) =>
        next => async (spec, ct) =>
        {
            long started = time.GetTimestamp();
            var result = await next(spec: spec, ct: ct).ConfigureAwait(false);
            var elapsed = time.GetElapsedTime(started);

            string? target = null;
            if (sensitive)
            {
                try
                {
                    target = spec.Path.Render();
                }
                catch (InvalidOperationException)
                {
                    // An unbound placeholder still deserves its log line; the template names it.
                }
            }

            sink(
                result.IsOk
                    ? new HttpLogEvent(
                        Verb: spec.Verb.Token(),
                        Route: spec.Path.Template,
                        Target: target,
                        Status: result.Value.Status,
                        Error: null,
                        Elapsed: elapsed,
                        FromCache: result.Value.FromCache,
                        BodyBytes: result.Value.BodyLength
                    )
                    : new HttpLogEvent(
                        Verb: spec.Verb.Token(),
                        Route: spec.Path.Template,
                        Target: target,
                        Status: null,
                        Error: result.Error,
                        Elapsed: elapsed,
                        FromCache: false,
                        BodyBytes: 0
                    )
            );

            return result;
        };
}
