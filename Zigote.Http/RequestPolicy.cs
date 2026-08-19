namespace Zigote.Http;

/// <summary>How one request treats the response cache.</summary>
public enum CacheMode
{
    /// <summary>Serve a fresh entry, revalidate a stale one, store what comes back. RFC 9111 rules.</summary>
    Default,

    /// <summary>Always revalidate with the origin, even when the stored entry is still fresh.</summary>
    Revalidate,

    /// <summary>Ignore the cache in both directions: no read, no store.</summary>
    Bypass,

    /// <summary>Go to the origin and store the result, but never serve from the cache.</summary>
    RefreshOnly,

    /// <summary>
    ///     Offline mode: serve from the cache or fail. A miss is
    ///     <see cref="PolicyFault.CacheMiss" />, never a silent network call.
    /// </summary>
    CacheOnly
}

/// <summary>
///     Retry and backoff for one request. Applies only when the verb is idempotent (or
///     <see cref="RequestPolicy.Idempotent" /> says so) and the body is replayable.
/// </summary>
/// <param name="MaxAttempts">Total attempts including the first. 1 disables retry.</param>
/// <param name="BaseDelay">First backoff step; doubles per attempt.</param>
/// <param name="MaxDelay">Ceiling on one backoff step.</param>
/// <param name="Jitter">
///     Fraction of the delay randomized (0 to 1). Without it, N clients that failed together retry
///     together — the thundering herd that turns a blip into an outage.
/// </param>
public readonly record struct RetryPolicy(
    int MaxAttempts = 3,
    TimeSpan BaseDelay = default,
    TimeSpan MaxDelay = default,
    double Jitter = 0.25)
{
    /// <summary>Three attempts, 200 ms base, 5 s ceiling.</summary>
    public static RetryPolicy Default { get; } =
        new(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));

    /// <summary>No retry.</summary>
    public static RetryPolicy None { get; } = new(1, TimeSpan.Zero, TimeSpan.Zero, 0);

    /// <summary>Backoff before attempt <paramref name="attempt" /> (1-based; attempt 1 waits nothing).</summary>
    public TimeSpan Backoff(int attempt, Random random)
    {
        if (attempt <= 1) return TimeSpan.Zero;
        var step = BaseDelay == default ? TimeSpan.FromMilliseconds(200) : BaseDelay;
        var max = MaxDelay == default ? TimeSpan.FromSeconds(5) : MaxDelay;
        double ms = Math.Min(step.TotalMilliseconds * Math.Pow(2, attempt - 2), max.TotalMilliseconds);
        double jitter = 1 - Jitter + (random.NextDouble() * Jitter * 2);
        return TimeSpan.FromMilliseconds(ms * Math.Clamp(jitter, 0, 2));
    }
}

/// <summary>Bytes moved so far on one attempt. <c>Total</c> is -1 when the length is unknown.</summary>
public readonly record struct HttpProgress(long Transferred, long Total, bool Uploading)
{
    /// <summary>0 to 1, or -1 when the total is unknown.</summary>
    public double Fraction => Total > 0 ? (double)Transferred / Total : -1;
}

/// <summary>
///     Per-request options — the one Dio idea worth keeping, implemented as a genuine field-wise
///     merge. Every field is <b>unset</b> until someone sets it, and resolution is three layers
///     deep and always in this order: the spec's own value, else the runner's
///     <see cref="HttpRunnerOptions.DefaultPolicy" />, else the built-in default named on the
///     <c>Effective*</c> accessor. So <c>spec.Deadline(5s)</c> customizes the deadline and
///     <i>only</i> the deadline — the runner's retry and cache defaults still apply, which is what
///     "per-request overrides base" has to mean for it to be predictable.
/// </summary>
public sealed record RequestPolicy
{
    /// <summary>Everything unset: the runner's defaults, then the built-ins, decide.</summary>
    public static RequestPolicy Default { get; } = new();

    /// <summary>
    ///     Budget for the whole logical call — retries, backoff and cache revalidation included.
    ///     Not a per-attempt timeout: three attempts under a 5 s deadline still finish in 5 s.
    /// </summary>
    public TimeSpan? Deadline { get; init; }

    /// <summary>Retry and backoff.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>Cache behaviour for this request.</summary>
    public CacheMode? Cache { get; init; }

    /// <summary>
    ///     Allow RFC 9111 heuristic freshness (guessing a lifetime from <c>Last-Modified</c>) for
    ///     responses that carry no explicit lifetime. Off unless set: guessing is the opposite of
    ///     predictable.
    /// </summary>
    public bool? AllowHeuristicFreshness { get; init; }

    /// <summary>
    ///     Force retry eligibility for a non-idempotent verb. Only the caller knows whether the
    ///     server dedupes a repeated POST, so only the caller can say this.
    /// </summary>
    public bool? Idempotent { get; init; }

    /// <summary>Skip the auth middleware for this request. For token endpoints and public CDNs.</summary>
    public bool? Anonymous { get; init; }

    /// <summary>
    ///     Hand back the response body as an open stream instead of buffering it. Disables retry
    ///     after the headers arrive and disables caching; the caller disposes the
    ///     <see cref="HttpResponse" />, which disposes the stream.
    /// </summary>
    public bool? Streaming { get; init; }

    /// <summary>Per-attempt transfer progress. Reported from the transport, which is the only layer that sees chunks.</summary>
    public IProgress<HttpProgress>? Progress { get; init; }

    // ── what the pipeline reads: the resolved answers ────────────────────────
    // Middleware never touches the nullable fields; by the time a spec enters the pipeline the
    // runner has already merged its DefaultPolicy in (WithFallback), so these accessors only ever
    // supply the built-in last resort.

    /// <summary>The deadline in force. Built-in: 30 s.</summary>
    public TimeSpan EffectiveDeadline => Deadline ?? TimeSpan.FromSeconds(30);

    /// <summary>The retry policy in force. Built-in: <see cref="RetryPolicy.Default" />.</summary>
    public RetryPolicy EffectiveRetry => Retry ?? RetryPolicy.Default;

    /// <summary>The cache mode in force. Built-in: <see cref="CacheMode.Default" />.</summary>
    public CacheMode EffectiveCache => Cache ?? CacheMode.Default;

    /// <summary>Whether heuristic freshness applies. Built-in: no.</summary>
    public bool HeuristicFreshnessAllowed => AllowHeuristicFreshness ?? false;

    /// <summary>Whether the caller declared a non-idempotent verb safe to repeat. Built-in: no.</summary>
    public bool IsIdempotent => Idempotent ?? false;

    /// <summary>Whether the auth layer is skipped. Built-in: no.</summary>
    public bool IsAnonymous => Anonymous ?? false;

    /// <summary>Whether the body comes back as an open stream. Built-in: no.</summary>
    public bool IsStreaming => Streaming ?? false;

    /// <summary>
    ///     This policy with <paramref name="baseline" /> filling every unset field — the merge the
    ///     runner applies once per request, so "set on the spec" always beats "set on the runner"
    ///     field by field rather than all-or-nothing.
    /// </summary>
    public RequestPolicy WithFallback(RequestPolicy baseline) => new()
    {
        Deadline = Deadline ?? baseline.Deadline,
        Retry = Retry ?? baseline.Retry,
        Cache = Cache ?? baseline.Cache,
        AllowHeuristicFreshness = AllowHeuristicFreshness ?? baseline.AllowHeuristicFreshness,
        Idempotent = Idempotent ?? baseline.Idempotent,
        Anonymous = Anonymous ?? baseline.Anonymous,
        Streaming = Streaming ?? baseline.Streaming,
        Progress = Progress ?? baseline.Progress
    };
}
