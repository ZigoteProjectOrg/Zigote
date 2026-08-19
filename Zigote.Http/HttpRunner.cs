using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using Zigote.Http.Cache;
using Zigote.Http.Transport;

namespace Zigote.Http;

/// <summary>
///     Everything a <see cref="HttpRunner" /> needs, as one value. Set what differs from the
///     defaults; the defaults are what a Zigote app should want.
/// </summary>
public sealed record HttpRunnerOptions
{
    /// <summary>Resolves relative routes. Leave null and every spec must carry an absolute URI.</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>
    ///     Sent with every request. A <c>User-Agent</c> is added automatically when this contains
    ///     none: plenty of CDNs answer a request carrying no User-Agent with a flat 403.
    /// </summary>
    public ImmutableArray<HeaderPair> DefaultHeaders { get; init; } = [];

    /// <summary>The default <c>User-Agent</c>, used when <see cref="DefaultHeaders" /> sets none.</summary>
    public string UserAgent { get; init; } = "Zigote/1.0 (+https://github.com/ZigoteProjectOrg/Zigote)";

    /// <summary>The response cache. Null disables the cache layer entirely — no key computation, no lookup.</summary>
    public IHttpCacheStore? Cache { get; init; }

    /// <summary>Where tokens come from. Null disables the auth layer.</summary>
    public IHttpAuthProvider? Auth { get; init; }

    /// <summary>
    ///     Extra layers of the caller's own — logging, header stamping, a mock that answers without
    ///     a network. They sit <b>just inside observability and just outside the cache</b>, which is
    ///     the one position that sees every logical call exactly once and can still tell a cache hit
    ///     from a network trip (<see cref="HttpResponse.FromCache" />). Composed in order: the first
    ///     is the outermost.
    /// </summary>
    public ImmutableArray<Middleware> Interceptors { get; init; } = [];

    /// <summary>
    ///     The clock behind deadlines, backoff and cache expiry. Inject a fake and every timing test
    ///     is deterministic; this is the whole reason nothing here calls <c>DateTime.UtcNow</c>.
    /// </summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Policy for specs that do not set their own.</summary>
    public RequestPolicy DefaultPolicy { get; init; } = RequestPolicy.Default;

    /// <summary>Consecutive failures against one host before its circuit opens.</summary>
    public int BreakerThreshold { get; init; } = 5;

    /// <summary>How long an open circuit stays open before one probe is let through.</summary>
    public TimeSpan BreakerCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Opt in to HTTP/3, falling back to h2 where QUIC is blocked. Off by default (design doc §14.1).</summary>
    public bool Http3 { get; init; }

    /// <summary>
    ///     The cookie jar, shared by every request through this runner. Null — the default — means
    ///     no cookie handling at all: a runner that silently retains session cookies makes request
    ///     history part of every later request's meaning, which is the opposite of a spec being a
    ///     value. Pass a <see cref="System.Net.CookieContainer" /> when the API actually uses
    ///     cookie sessions. Ignored on browser targets, where the browser owns the jar.
    /// </summary>
    public System.Net.CookieContainer? Cookies { get; init; }

    /// <summary>
    ///     Redirects the transport follows per request, still inside the call's deadline. 0 turns
    ///     following off, so a 3xx comes back as the answer it is. The handler strips
    ///     <c>Authorization</c> when a redirect leaves the origin; past the cap it hands back the
    ///     last 3xx rather than erroring.
    /// </summary>
    public int MaxRedirects { get; init; } = 10;

    /// <summary>
    ///     At most this many requests in flight per host; the rest queue, inside their own
    ///     deadlines. 0 — the default — means no gate. Browsers settle around 6; an image grid
    ///     wants that here too, so a thousand submitted tiles queue instead of stampeding one CDN.
    /// </summary>
    public int MaxConcurrencyPerHost { get; init; }

    /// <summary>
    ///     One structured <see cref="HttpLogEvent" /> per logical call, when set. A delegate rather
    ///     than an <c>ILogger</c> so this assembly stays dependency-free — the bridge is one lambda.
    ///     Redacts by default; see <see cref="EnableSensitiveLogging" />.
    /// </summary>
    public Action<HttpLogEvent>? OnLog { get; init; }

    /// <summary>
    ///     Last-resort access to the handler this runner creates: proxies, client certificates, a
    ///     custom trust chain — the enterprise knobs this library refuses to model one by one. Runs
    ///     once, after the defaults are set, before the first request. Ignored when
    ///     <see cref="Transport" /> is injected or the platform has no <c>SocketsHttpHandler</c>.
    /// </summary>
    public Action<System.Net.Http.SocketsHttpHandler>? ConfigureHandler { get; init; }

    /// <summary>
    ///     Let the rendered path and query string reach spans, alongside the always-present route
    ///     template. Off, obviously: a query string is where tokens and user identifiers end up.
    ///     <c>Authorization</c> never reaches a tag regardless.
    /// </summary>
    public bool EnableSensitiveLogging { get; init; }

    /// <summary>
    ///     Replaces the transport. A test passes a lambda here and never opens a socket; a platform
    ///     with its own handler passes one wrapping it.
    /// </summary>
    public Send? Transport { get; init; }
}

/// <summary>
///     A composed pipeline plus the transport under it: the thing you actually call. One per origin
///     per app, long-lived — the pipeline is composed in the constructor and never rebuilt, and the
///     connection pool lives as long as this does.
/// </summary>
/// <example>
///     <code>
///     var runner = new HttpRunner(new HttpRunnerOptions
///     {
///         BaseAddress = new Uri("https://assets.example.com/"),
///         Cache = new FileCacheStore(FileCacheStore.DefaultDirectory)
///     });
///
///     var result = await runner.JsonAsync(
///         Http.Get("assets/{id}").Route("id", id).Deadline(TimeSpan.FromSeconds(5)),
///         AssetJson.Default.AssetMeta);
///
///     if (result.TryGet(out var meta, out var error)) Use(meta); else Log(error.Message);
///     </code>
/// </example>
public sealed class HttpRunner : IDisposable
{
    /// <summary>
    ///     How much of an error body <see cref="HttpError.Status" /> keeps. Enough for any real API
    ///     error payload; a misbehaving origin sending megabytes of HTML with its 500 must not get
    ///     to double-buffer them into every caller's error value.
    /// </summary>
    private const int ErrorBodyCap = 64 * 1024;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpRunnerOptions _options;
    private readonly Send _send;
    private readonly HttpTransport? _transport;

    /// <summary>A runner over <paramref name="options" />.</summary>
    public HttpRunner(HttpRunnerOptions options)
    {
        _options = options;

        Send transport;
        if (options.Transport is { } injected)
        {
            transport = injected;
            Capabilities = new HttpCapabilities(true, true, false, options.Cache is FileCacheStore);
        }
        else
        {
            _transport = new HttpTransport(
                options.BaseAddress, WithUserAgent(options), options.Http3,
                options.Cookies, options.MaxRedirects, options.ConfigureHandler);
            transport = _transport.AsSend();
            Capabilities = _transport.Capabilities;
        }

        // The fixed order from the design doc §4.3, and the order is the semantics. Composed once,
        // here — never per request.
        var layers = ImmutableArray.CreateBuilder<Middleware>(10);
        layers.Add(DeadlineMiddleware.Create(options.Time));
        layers.Add(ObservabilityMiddleware.Create(options.Time, options.EnableSensitiveLogging));
        if (options.OnLog is { } sink)
            layers.Add(LoggingMiddleware.Create(sink, options.Time, options.EnableSensitiveLogging));
        if (!options.Interceptors.IsDefaultOrEmpty) layers.AddRange(options.Interceptors);
        if (options.Cache is { } cache)
            layers.Add(CacheMiddleware.Create(cache, options.BaseAddress, options.Time, _lifetime.Token));
        layers.Add(DedupMiddleware.Create(options.BaseAddress));
        if (options.MaxConcurrencyPerHost > 0)
            layers.Add(ConcurrencyMiddleware.Create(options.MaxConcurrencyPerHost, options.BaseAddress));
        layers.Add(RetryMiddleware.Create(
            options.Time, options.BaseAddress, options.BreakerThreshold, options.BreakerCooldown));
        if (options.Auth is { } auth) layers.Add(AuthMiddleware.Create(auth));

        _send = Pipeline.Build(transport, layers.ToImmutable().AsSpan());
    }

    /// <summary>A runner against <paramref name="baseAddress" /> with an in-memory cache. The two-line setup.</summary>
    public HttpRunner(string baseAddress)
        : this(new HttpRunnerOptions { BaseAddress = new Uri(baseAddress), Cache = new MemoryCacheStore() })
    {
    }

    /// <summary>What this platform's transport can do. Check before asking for ranges or streaming uploads.</summary>
    public HttpCapabilities Capabilities { get; }

    /// <summary>The base address relative routes resolve against.</summary>
    public Uri? BaseAddress => _options.BaseAddress;

    /// <summary>The clock this runner was built with. Middleware and <see cref="HttpFile" /> share it.</summary>
    public TimeProvider Time => _options.Time;

    /// <inheritdoc />
    public void Dispose()
    {
        // Stop the background stale-while-revalidate refreshes first, then take the sockets away.
        _lifetime.Cancel();
        _lifetime.Dispose();
        _transport?.Dispose();
    }

    /// <summary>
    ///     Runs one spec through the whole pipeline. The response is the caller's to dispose, and a
    ///     4xx or 5xx comes back as a successful <see cref="HttpResult{T}" /> carrying that response
    ///     — only the typed helpers below turn a status into an <see cref="HttpError.Status" />,
    ///     because only they know the caller wanted a value rather than an answer.
    /// </summary>
    public ValueTask<HttpResult<HttpResponse>> SendAsync(HttpSpec spec, CancellationToken ct = default) =>
        // Field-wise, not all-or-nothing: a spec that set only its deadline still gets the runner's
        // retry and cache defaults. Skipped entirely when the runner configured nothing.
        _send(ReferenceEquals(_options.DefaultPolicy, RequestPolicy.Default)
            ? spec
            : spec with { Policy = spec.Policy.WithFallback(_options.DefaultPolicy) }, ct);

    /// <summary>The body bytes of a 2xx, or an error. Non-2xx becomes <see cref="HttpError.Status" />.</summary>
    public ValueTask<HttpResult<byte[]>> BytesAsync(HttpSpec spec, CancellationToken ct = default) =>
        ReadAsync(spec, static r => HttpResult<byte[]>.Ok(r.Body.ToArray()), ct);

    /// <summary>The body of a 2xx as UTF-8 text.</summary>
    public ValueTask<HttpResult<string>> TextAsync(HttpSpec spec, CancellationToken ct = default) =>
        ReadAsync(spec, static r => HttpResult<string>.Ok(r.Text()), ct);

    /// <summary>
    ///     The body of a 2xx, deserialized with a source-generated contract. Requiring the
    ///     <see cref="JsonTypeInfo{T}" /> rather than options is what keeps callers trim- and
    ///     AOT-clean; generated clients pass theirs automatically.
    /// </summary>
    public ValueTask<HttpResult<T>> JsonAsync<T>(
        HttpSpec spec, JsonTypeInfo<T> typeInfo, CancellationToken ct = default) =>
        ReadAsync(spec, r => r.Json(typeInfo), ct);

    /// <summary>
    ///     The one shape all typed helpers share: send, dispose the response before returning, turn
    ///     a non-2xx into <see cref="HttpError.Status" />, and let <paramref name="read" /> say what
    ///     a 2xx body means. Written once so the disposal and status rules cannot drift apart.
    /// </summary>
    private async ValueTask<HttpResult<T>> ReadAsync<T>(
        HttpSpec spec, Func<HttpResponse, HttpResult<T>> read, CancellationToken ct)
    {
        var result = await SendAsync(spec, ct).ConfigureAwait(false);
        if (!result.IsOk) return HttpResult<T>.Fail(result.Error);

        using var response = result.Value;
        return response.IsSuccess ? read(response) : HttpResult<T>.Fail(StatusError(response));
    }

    /// <summary>
    ///     The response body as an open stream, for a download that should not be buffered. The
    ///     caller disposes the returned <see cref="HttpResponse" />, which closes the stream and
    ///     releases the connection.
    /// </summary>
    public async ValueTask<HttpResult<HttpResponse>> StreamAsync(HttpSpec spec, CancellationToken ct = default)
    {
        var result = await SendAsync(spec with { Policy = spec.Policy with { Streaming = true } }, ct)
            .ConfigureAwait(false);
        if (!result.IsOk || result.Value.IsSuccess) return result;

        using var response = result.Value;
        return HttpResult<HttpResponse>.Fail(StatusError(response));
    }

    private static HttpError StatusError(HttpResponse response) =>
        // Capped: the error body is for a log line or an error dialog, and a misbehaving origin's
        // multi-megabyte 500 page must not be copied in full into every caller's error value.
        new HttpError.Status(response.Status,
            response.Body[..Math.Min(response.BodyLength, ErrorBodyCap)].ToArray());

    private static ImmutableArray<HeaderPair> WithUserAgent(HttpRunnerOptions options)
    {
        var headers = options.DefaultHeaders.IsDefault ? ImmutableArray<HeaderPair>.Empty : options.DefaultHeaders;
        foreach (var h in headers)
            if (string.Equals(h.Name, "User-Agent", StringComparison.OrdinalIgnoreCase))
                return headers;
        return headers.Add(new HeaderPair("User-Agent", options.UserAgent));
    }
}
