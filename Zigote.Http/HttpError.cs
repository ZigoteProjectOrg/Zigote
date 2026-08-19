using System.Net;

namespace Zigote.Http;

/// <summary>Which part of the connection failed. The distinction retry and telemetry care about.</summary>
public enum TransportFault
{
    /// <summary>Name resolution failed.</summary>
    Dns,

    /// <summary>TCP/QUIC connect failed — refused, unreachable, no route.</summary>
    Connect,

    /// <summary>TLS handshake or certificate validation failed. Never retried: it will fail again.</summary>
    Tls,

    /// <summary>The connection died mid-exchange.</summary>
    Reset,

    /// <summary>Something else the platform reported.</summary>
    Unknown
}

/// <summary>
///     Where the clock ran out. Two stages, because two clocks exist: the transport's connect
///     timeout, and the whole-call deadline. Per-stage header/body timeouts were dropped rather
///     than left as enum members nothing produces — the deadline already bounds a stalled read.
/// </summary>
public enum TimeoutStage
{
    /// <summary>Before the connection was established (the transport's own connect timeout).</summary>
    Connect,

    /// <summary>The whole-call deadline, retries included.</summary>
    Total
}

/// <summary>A refusal by this library rather than by the network.</summary>
public enum PolicyFault
{
    /// <summary>The circuit breaker for this host is open; the request was never sent.</summary>
    CircuitOpen,

    /// <summary><see cref="CacheMode.CacheOnly" /> and nothing usable was stored.</summary>
    CacheMiss,

    /// <summary>
    ///     The transport on this platform cannot do what the request needs — ranges on WASM, or a
    ///     stream upload where the transport can only send buffered bodies.
    /// </summary>
    Unsupported
}

/// <summary>
///     Everything that can go wrong, as a value. Nothing in this library throws for an outcome it
///     expects — a 404 is data, a DNS failure is data, and a caller that wants an exception asks for
///     one with <see cref="HttpResult{T}.Unwrap" />.
/// </summary>
public abstract record HttpError
{
    private HttpError() { }

    /// <summary>A one-line description for logs. Never contains the body or the Authorization header.</summary>
    public abstract string Message { get; }

    /// <summary>
    ///     Whether repeating the request could plausibly succeed. Retry consults this; it is not a
    ///     promise, only the difference between "the network hiccuped" and "the certificate is wrong".
    /// </summary>
    public virtual bool IsTransient => false;

    /// <summary>The connection failed.</summary>
    public sealed record Transport(TransportFault Fault, Exception Inner) : HttpError
    {
        /// <inheritdoc />
        public override string Message => $"transport {Fault}: {Inner.Message}";

        /// <inheritdoc />
        public override bool IsTransient => Fault is not TransportFault.Tls;
    }

    /// <summary>The budget ran out.</summary>
    public sealed record Timeout(TimeSpan Budget, TimeoutStage Stage) : HttpError
    {
        /// <inheritdoc />
        public override string Message => $"timeout at {Stage} after {Budget.TotalMilliseconds:F0} ms";

        /// <summary>A whole-call deadline is final; a per-attempt timeout is worth another attempt.</summary>
        public override bool IsTransient => Stage is not TimeoutStage.Total;
    }

    /// <summary>The caller's <see cref="CancellationToken" /> fired.</summary>
    public sealed record Canceled : HttpError
    {
        /// <inheritdoc />
        public override string Message => "canceled";
    }

    /// <summary>
    ///     The origin answered, with a status the caller asked to treat as failure (4xx/5xx by
    ///     default). <see cref="Body" /> is the error body, already read — that is usually where the
    ///     API puts the reason.
    /// </summary>
    public sealed record Status(HttpStatusCode Code, byte[] Body) : HttpError
    {
        /// <inheritdoc />
        public override string Message => $"HTTP {(int)Code} {Code}";

        /// <summary>429 and 5xx are worth another attempt; 4xx is the caller's bug.</summary>
        public override bool IsTransient => IsTransientCode(Code);

        /// <summary>
        ///     The one definition of "this status might succeed next time". The retry layer asks
        ///     the same question about statuses that arrive as responses rather than errors, so it
        ///     lives here once instead of drifting apart in two files.
        /// </summary>
        public static bool IsTransientCode(HttpStatusCode code) =>
            code is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ||
            ((int)code is >= 500 and < 600 && code != HttpStatusCode.NotImplemented);

        /// <summary>The error body as UTF-8 text, for a log line or an error dialog.</summary>
        public string BodyText() => System.Text.Encoding.UTF8.GetString(Body);
    }

    /// <summary>The response arrived but did not deserialize into what the caller asked for.</summary>
    public sealed record Decode(Type Target, Exception Inner) : HttpError
    {
        /// <inheritdoc />
        public override string Message => $"cannot decode {Target.Name}: {Inner.Message}";
    }

    /// <summary>This library refused before or instead of sending.</summary>
    public sealed record Policy(PolicyFault Fault) : HttpError
    {
        /// <inheritdoc />
        public override string Message => $"policy {Fault}";
    }
}

/// <summary>
///     The exception shape of an <see cref="HttpError" />, thrown only where a caller explicitly
///     asks for it: <see cref="HttpResult{T}.Unwrap" /> and the <c>OrThrow</c> half of a generated
///     client. Nothing inside the library throws this at itself.
/// </summary>
public sealed class HttpException(HttpError error)
    : Exception(error.Message, (error as HttpError.Transport)?.Inner ?? (error as HttpError.Decode)?.Inner)
{
    /// <summary>The error this exception carries, with its full structure intact.</summary>
    public HttpError Error { get; } = error;
}
