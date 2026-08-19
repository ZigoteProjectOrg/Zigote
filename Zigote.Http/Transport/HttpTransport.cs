using System.Buffers;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Zigote.Http.Transport;

/// <summary>
///     What the transport on this platform can actually do. Features degrade by <i>reporting</i>,
///     never by silently doing something slower — <see cref="HttpFile" /> on a transport without
///     <see cref="Ranges" /> fails at open rather than quietly downloading four gigabytes.
/// </summary>
/// <param name="Ranges">Range requests are honoured end to end (they are not on browser fetch through a cache).</param>
/// <param name="StreamingUpload">A request body may be streamed rather than buffered.</param>
/// <param name="ConnectionPooling">Connections are pooled and kept alive by us rather than by the platform.</param>
/// <param name="PersistentCache">A disk-backed cache store is usable (it is not under WASM).</param>
public readonly record struct HttpCapabilities(
    bool Ranges,
    bool StreamingUpload,
    bool ConnectionPooling,
    bool PersistentCache);

/// <summary>
///     The bottom of the stack: a <see cref="Send" /> over the BCL's message invoker. This is the
///     only file in the library that knows <c>HttpRequestMessage</c> exists.
/// </summary>
/// <remarks>
///     We keep the part of <c>IHttpClientFactory</c> that matters — one long-lived handler with a
///     bounded <c>PooledConnectionLifetime</c> so DNS changes are picked up — and own it explicitly
///     rather than inheriting a handler pipeline we replaced with <see cref="Middleware" />.
/// </remarks>
public sealed class HttpTransport : IDisposable
{
    private readonly Uri? _baseAddress;
    private readonly ImmutableArray<HeaderPair> _defaultHeaders;
    private readonly HttpMessageInvoker _invoker;
    private readonly bool _ownsInvoker;
    private readonly bool _http3;

    /// <summary>A transport over a handler this instance creates and owns.</summary>
    /// <param name="baseAddress">Resolves relative routes. Null means every spec must carry an absolute URI.</param>
    /// <param name="defaultHeaders">Sent with every request unless the spec overrides the name.</param>
    /// <param name="http3">HTTP/3 opt-in. Off by default until we have field data on QUIC blocking.</param>
    /// <param name="cookies">
    ///     The cookie jar, or null for no cookie handling at all. Explicit on purpose: a handler
    ///     that silently retains session cookies turns "the same request" into "a different request
    ///     depending on history", which is the opposite of a spec being a value.
    /// </param>
    /// <param name="maxRedirects">Redirects the handler follows per request. 0 hands 3xx back to the caller.</param>
    /// <param name="configureHandler">Last-resort handler access (proxies, client certs). Runs once, after the defaults.</param>
    public HttpTransport(
        Uri? baseAddress = null,
        ImmutableArray<HeaderPair> defaultHeaders = default,
        bool http3 = false,
        CookieContainer? cookies = null,
        int maxRedirects = 10,
        Action<SocketsHttpHandler>? configureHandler = null)
        : this(CreateInvoker(cookies, maxRedirects, configureHandler), ownsInvoker: true, baseAddress, defaultHeaders) =>
        _http3 = http3;

    /// <summary>A transport over an invoker someone else owns — a test double, or a platform handler.</summary>
    public HttpTransport(
        HttpMessageInvoker invoker,
        bool ownsInvoker,
        Uri? baseAddress = null,
        ImmutableArray<HeaderPair> defaultHeaders = default)
    {
        _invoker = invoker;
        _ownsInvoker = ownsInvoker;
        _baseAddress = baseAddress;
        _defaultHeaders = defaultHeaders.IsDefault ? [] : defaultHeaders;
        Capabilities = Probe();
    }

    /// <summary>What this platform's transport supports.</summary>
    public HttpCapabilities Capabilities { get; }

    /// <summary>The base address relative routes resolve against.</summary>
    public Uri? BaseAddress => _baseAddress;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsInvoker) _invoker.Dispose();
    }

    /// <summary>This transport as the innermost <see cref="Send" />.</summary>
    public Send AsSend() => SendAsync;

    private static HttpMessageInvoker CreateInvoker(
        CookieContainer? cookies, int maxRedirects, Action<SocketsHttpHandler>? configureHandler)
    {
        // SocketsHttpHandler is unsupported on browser/WASM and is bypassed on iOS/Android when the
        // app opts into the native handler; HttpClientHandler resolves to whatever the platform has.
        // Cookie/redirect knobs are not pushed onto it — the browser owns both there anyway.
        if (!SocketsHttpHandler.IsSupported) return new HttpMessageInvoker(new HttpClientHandler());

        var handler = new SocketsHttpHandler
        {
            // Two minutes: long enough that a busy client reuses connections, short enough that a
            // DNS change or a blue/green swap is picked up without a restart.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            // Redirects are followed by the handler (the deadline still bounds the whole chain),
            // and it already strips Authorization when a redirect leaves the origin. Past the cap
            // the handler stops and hands back the last 3xx — an answer, not an error.
            AllowAutoRedirect = maxRedirects > 0,
            MaxAutomaticRedirections = Math.Max(1, maxRedirects),
            // No cookie jar unless the app supplies one. The BCL default is a silent shared jar,
            // which makes request history part of every later request's meaning.
            UseCookies = cookies is not null
        };
        if (cookies is not null) handler.CookieContainer = cookies;
        // The escape hatch runs last so it can override anything above — that is its whole point.
        configureHandler?.Invoke(handler);
        return new HttpMessageInvoker(handler);
    }

    private HttpCapabilities Probe()
    {
        bool sockets = SocketsHttpHandler.IsSupported;
        bool browser = OperatingSystem.IsBrowser();
        return new HttpCapabilities(
            Ranges: !browser,
            StreamingUpload: !browser,
            ConnectionPooling: sockets,
            PersistentCache: !browser);
    }

    private async ValueTask<HttpResult<HttpResponse>> SendAsync(HttpSpec spec, CancellationToken ct)
    {
        // Non-replayable means "contains a stream" — the only body kind a transport can be unable
        // to send. Refusing here is the capability contract: degrade by reporting, never by
        // silently buffering four gigabytes to make the request possible.
        if (!spec.Body.IsReplayable && !Capabilities.StreamingUpload)
            return new HttpError.Policy(PolicyFault.Unsupported);

        HttpRequestMessage request;
        try
        {
            request = BuildRequest(spec);
        }
        catch (InvalidOperationException e)
        {
            return new HttpError.Transport(TransportFault.Unknown, e);
        }

        HttpResponseMessage? message = null;
        try
        {
            // ResponseHeadersRead, always: the body is ours to read, so progress, the deadline and
            // streaming responses all work the same way instead of three ways.
            message = await _invoker.SendAsync(request, ct).ConfigureAwait(false);
            var headers = CollectHeaders(message);

            if (spec.Policy.IsStreaming)
            {
                var stream = await message.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var streamed = HttpResponse.FromStream(message.StatusCode, headers, stream);
                message = null; // ownership moved to the response, which disposes the stream
                return streamed;
            }

            long declared = message.Content.Headers.ContentLength ?? -1;
            var (buffer, length) = await ReadBodyAsync(message, declared, spec.Policy.Progress, ct)
                .ConfigureAwait(false);
            return HttpResponse.FromPooled(message.StatusCode, headers, buffer, length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new HttpError.Canceled();
        }
        catch (OperationCanceledException)
        {
            // Not our token: the handler's own ConnectTimeout fired.
            return new HttpError.Timeout(TimeSpan.FromSeconds(10), TimeoutStage.Connect);
        }
        catch (HttpRequestException e)
        {
            return new HttpError.Transport(Classify(e), e);
        }
        catch (IOException e)
        {
            return new HttpError.Transport(TransportFault.Reset, e);
        }
        finally
        {
            message?.Dispose();
            request.Dispose();
        }
    }

    private static TransportFault Classify(HttpRequestException e) => e.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => TransportFault.Dns,
        HttpRequestError.ConnectionError => TransportFault.Connect,
        HttpRequestError.SecureConnectionError => TransportFault.Tls,
        HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError => TransportFault.Reset,
        _ => TransportFault.Unknown
    };

    private static async ValueTask<(byte[] Buffer, int Length)> ReadBodyAsync(
        HttpResponseMessage message, long declared, IProgress<HttpProgress>? progress, CancellationToken ct)
    {
        var stream = await message.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(declared > 0 ? (int)Math.Min(declared, int.MaxValue) : 8192);
        int length = 0;

        while (true)
        {
            if (length == buffer.Length)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                buffer.AsSpan(0, length).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = bigger;
            }

            int read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            progress?.Report(new HttpProgress(length, declared, Uploading: false));
        }

        return (buffer, length);
    }

    private static ImmutableArray<HeaderPair> CollectHeaders(HttpResponseMessage message)
    {
        var builder = ImmutableArray.CreateBuilder<HeaderPair>(8);
        foreach (var h in message.Headers) Add(builder, h);
        foreach (var h in message.Content.Headers) Add(builder, h);
        return builder.ToImmutable();

        static void Add(ImmutableArray<HeaderPair>.Builder into, KeyValuePair<string, IEnumerable<string>> header)
        {
            foreach (string value in header.Value) into.Add(new HeaderPair(header.Key, value));
        }
    }

    private HttpRequestMessage BuildRequest(HttpSpec spec)
    {
        var request = new HttpRequestMessage(spec.Verb.ToMethod(), spec.ResolveUri(_baseAddress));

        foreach (var h in _defaultHeaders)
            request.Headers.TryAddWithoutValidation(h.Name, h.Value);
        if (!spec.Headers.IsDefaultOrEmpty)
            foreach (var h in spec.Headers)
                request.Headers.TryAddWithoutValidation(h.Name, h.Value);

        if (_http3)
        {
            // Opt-in, and only ever a request: RequestVersionOrLower falls back to h2 rather than
            // failing on a network that blocks QUIC.
            request.Version = System.Net.HttpVersion.Version30;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        // W3C trace context: without this, distributed traces die at the client — the span from
        // the observability layer is Activity.Current here, so its ids go on the wire. The BCL
        // propagator skips fields already present, so a hand-set traceparent header wins.
        if (System.Diagnostics.Activity.Current is { } activity)
            System.Diagnostics.DistributedContextPropagator.Current.Inject(activity, request,
                static (carrier, name, value) =>
                    ((HttpRequestMessage)carrier!).Headers.TryAddWithoutValidation(name, value));

        var content = BuildContent(spec.Body);
        if (content is not null && spec.Policy.Progress is { } progress)
            content = new ProgressContent(content, progress);
        request.Content = content;
        return request;
    }

    private static HttpContent? BuildContent(HttpBody body)
    {
        switch (body)
        {
            case HttpBody.NoBody:
                return null;
            case HttpBody.BytesBody b:
            {
                var content = new ReadOnlyMemoryContent(b.Content);
                SetContentType(content, b.ContentType);
                return content;
            }
            case HttpBody.FormBody f:
            {
                var sb = new StringBuilder();
                foreach (var field in f.Fields.IsDefault ? ImmutableArray<QueryParam>.Empty : f.Fields)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(Uri.EscapeDataString(field.Name)).Append('=')
                        .Append(Uri.EscapeDataString(field.Value));
                }

                var content = new ByteArrayContent(Encoding.UTF8.GetBytes(sb.ToString()));
                SetContentType(content, "application/x-www-form-urlencoded");
                return content;
            }
            case HttpBody.StreamBody s:
            {
                var content = new StreamContent(s.Content);
                SetContentType(content, s.ContentType);
                return content;
            }
            case HttpBody.MultipartBody m:
            {
                // MultipartFormDataContent writes the boundary framing; each part's headers come
                // from the same BuildContent that made it, so a part is encoded exactly as the
                // same body would be on its own.
                var content = new MultipartFormDataContent();
                foreach (var part in m.Parts)
                {
                    var inner = BuildContent(part.Content);
                    if (inner is null) continue; // an HttpBody.None part says nothing
                    if (part.FileName is { } fileName) content.Add(inner, part.Name, fileName);
                    else content.Add(inner, part.Name);
                }

                return content;
            }
            default:
                throw new InvalidOperationException($"Unknown body {body.GetType().Name}.");
        }
    }

    private static void SetContentType(HttpContent content, string contentType)
    {
        if (MediaTypeHeaderValue.TryParse(contentType, out var parsed)) content.Headers.ContentType = parsed;
    }

    /// <summary>
    ///     Wraps any built content and reports bytes as they leave. Upload progress lives here, in
    ///     the transport, because this is the only layer that sees the wire — and it is per attempt,
    ///     so a retried upload visibly starts over instead of jumping past 100%.
    /// </summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly IProgress<HttpProgress> _progress;

        public ProgressContent(HttpContent inner, IProgress<HttpProgress> progress)
        {
            _inner = inner;
            _progress = progress;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _inner.CopyToAsync(new CountingStream(stream, _inner.Headers.ContentLength ?? -1, _progress));

        protected override bool TryComputeLength(out long length)
        {
            long? known = _inner.Headers.ContentLength;
            length = known ?? 0;
            return known.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Write-only pass-through that counts. Never disposes the wire stream — the handler owns it.</summary>
    private sealed class CountingStream(Stream inner, long total, IProgress<HttpProgress> progress) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Report(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            Report(buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Report(int count)
        {
            _written += count;
            progress.Report(new HttpProgress(_written, total, Uploading: true));
        }
    }
}
