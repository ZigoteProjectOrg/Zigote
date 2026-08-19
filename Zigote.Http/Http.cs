using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Zigote.Http.Frame;

namespace Zigote.Http;

/// <summary>
///     The entry point for building requests. Pure — <c>HttpRequest.Get(...)</c> returns a value,
///     and every transform below returns a new one. Nothing is sent until a
///     <see cref="HttpRunner" /> runs it, which is what makes a request loggable, cacheable and
///     testable before it exists on a wire.
/// </summary>
/// <remarks>
///     The design doc called this <c>Http</c>. It cannot be: a type named <c>Zigote.Http.Http</c>
///     is shadowed by the <c>Zigote.Http</c> namespace for every caller that itself lives under
///     <c>Zigote</c> — which is all of them. The frame queue moved to <see cref="HttpFrame" /> in
///     the same breath, and the two surfaces read better apart anyway.
/// </remarks>
public static class HttpRequest
{
    /// <summary>A GET for <paramref name="template" />, which may contain <c>{placeholders}</c>.</summary>
    public static HttpSpec Get(string template) => HttpSpec.For(HttpVerb.Get, template);

    /// <summary>A HEAD.</summary>
    public static HttpSpec Head(string template) => HttpSpec.For(HttpVerb.Head, template);

    /// <summary>A POST.</summary>
    public static HttpSpec Post(string template) => HttpSpec.For(HttpVerb.Post, template);

    /// <summary>A PUT.</summary>
    public static HttpSpec Put(string template) => HttpSpec.For(HttpVerb.Put, template);

    /// <summary>A PATCH.</summary>
    public static HttpSpec Patch(string template) => HttpSpec.For(HttpVerb.Patch, template);

    /// <summary>A DELETE.</summary>
    public static HttpSpec Delete(string template) => HttpSpec.For(HttpVerb.Delete, template);

}

/// <summary>
///     The frame loop's submit/poll queue, as a host-assigned provider: assigned at startup, faked
///     in tests, the same shape <c>Input</c> and <c>Audio</c> already use. Every method here
///     allocates nothing, so widgets and gameplay code can use them inside Measure→Layout→Paint.
/// </summary>
public static class HttpFrame
{
    /// <summary>The queue these methods forward to. Null until the host assigns one.</summary>
    public static FrameHttpQueue? Queue { get; set; }

    /// <summary>Queues a request from the frame path. Invalid handle when there is no queue or it is full.</summary>
    public static HttpHandle Submit(HttpSpec spec) => Queue?.Submit(spec) ?? default;

    /// <summary>Polls a submitted request. Allocates nothing.</summary>
    public static bool TryTake(HttpHandle handle, out HttpOutcome outcome)
    {
        var queue = Queue;
        if (queue is not null) return queue.TryTake(handle, out outcome);
        outcome = default;
        return false;
    }

    /// <summary>Asks a submitted request to stop.</summary>
    public static void Cancel(HttpHandle handle) => Queue?.Cancel(handle);

    /// <summary>Frees the slot behind a taken handle, and its body buffer with it.</summary>
    public static void Release(HttpHandle handle) => Queue?.Release(handle);
}

/// <summary>
///     The fluent surface: pure transforms over a <see cref="HttpSpec" />, one per thing a request
///     can carry. Named <c>With*</c> where the spec already has a property of that name, so the
///     builder and the data never shadow each other.
/// </summary>
public static class HttpSpecExtensions
{
    /// <summary>Binds a <c>{placeholder}</c> in the route template. The value is percent-encoded at render time.</summary>
    public static HttpSpec Route(this HttpSpec spec, string name, string value) =>
        spec with { Path = spec.Path.Bind(name, value) };

    /// <summary>Binds a <c>{placeholder}</c> from anything formattable, invariantly.</summary>
    public static HttpSpec Route<T>(this HttpSpec spec, string name, T value) where T : IFormattable =>
        spec.Route(name, value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary>Appends a query-string pair.</summary>
    public static HttpSpec WithQuery(this HttpSpec spec, string name, string value) =>
        spec with { Query = Safe(spec.Query).Add(new QueryParam(name, value)) };

    /// <summary>Appends a query-string pair from anything formattable, invariantly.</summary>
    public static HttpSpec WithQuery<T>(this HttpSpec spec, string name, T value) where T : IFormattable =>
        spec.WithQuery(name, value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary>Appends a query-string pair only when <paramref name="value" /> is not null. For optional filters.</summary>
    public static HttpSpec WithQueryIf(this HttpSpec spec, string name, string? value) =>
        value is null ? spec : spec.WithQuery(name, value);

    /// <summary>Adds a request header.</summary>
    public static HttpSpec WithHeader(this HttpSpec spec, string name, string value) =>
        spec with { Headers = Safe(spec.Headers).Add(new HeaderPair(name, value)) };

    /// <summary>Sets the body.</summary>
    public static HttpSpec WithBody(this HttpSpec spec, HttpBody body) => spec with { Body = body };

    /// <summary>Sets a JSON body, serialized now with a source-generated contract.</summary>
    public static HttpSpec WithJson<T>(this HttpSpec spec, T value, JsonTypeInfo<T> typeInfo) =>
        spec with { Body = HttpBody.Json(value, typeInfo) };

    /// <summary>Sets a form body.</summary>
    public static HttpSpec WithForm(this HttpSpec spec, params ReadOnlySpan<QueryParam> fields) =>
        spec with { Body = HttpBody.Form([..fields]) };

    /// <summary>
    ///     Sets a <c>multipart/form-data</c> body. Build parts with
    ///     <see cref="MultipartPart.Field" /> and <see cref="MultipartPart.File(string, string, ReadOnlyMemory{byte}, string)" />;
    ///     a request whose parts are all replayable stays retryable, one stream part makes it one-shot.
    /// </summary>
    public static HttpSpec WithMultipart(this HttpSpec spec, params ReadOnlySpan<MultipartPart> parts) =>
        spec with { Body = HttpBody.Multipart(parts) };

    /// <summary>Sets the budget for the whole call — retries and revalidation included.</summary>
    public static HttpSpec Deadline(this HttpSpec spec, TimeSpan budget) =>
        spec with { Policy = spec.Policy with { Deadline = budget } };

    /// <summary>Sets retry and backoff.</summary>
    public static HttpSpec Retry(this HttpSpec spec, RetryPolicy retry) =>
        spec with { Policy = spec.Policy with { Retry = retry } };

    /// <summary>Turns retry off for this request.</summary>
    public static HttpSpec NoRetry(this HttpSpec spec) => spec.Retry(RetryPolicy.None);

    /// <summary>Sets how this request treats the cache.</summary>
    public static HttpSpec Cache(this HttpSpec spec, CacheMode mode) =>
        spec with { Policy = spec.Policy with { Cache = mode } };

    /// <summary>Skips the cache in both directions.</summary>
    public static HttpSpec NoCache(this HttpSpec spec) => spec.Cache(CacheMode.Bypass);

    /// <summary>Declares that repeating this request is safe, so a non-idempotent verb may be retried.</summary>
    public static HttpSpec Idempotent(this HttpSpec spec) =>
        spec with { Policy = spec.Policy with { Idempotent = true } };

    /// <summary>Sends this request without an <c>Authorization</c> header.</summary>
    public static HttpSpec Anonymous(this HttpSpec spec) =>
        spec with { Policy = spec.Policy with { Anonymous = true } };

    /// <summary>Hands the body back as an open stream instead of buffering it.</summary>
    public static HttpSpec Streaming(this HttpSpec spec) =>
        spec with { Policy = spec.Policy with { Streaming = true } };

    /// <summary>Reports transfer progress for each attempt.</summary>
    public static HttpSpec Progress(this HttpSpec spec, IProgress<HttpProgress> progress) =>
        spec with { Policy = spec.Policy with { Progress = progress } };

    /// <summary>Replaces the whole policy.</summary>
    public static HttpSpec WithPolicy(this HttpSpec spec, RequestPolicy policy) => spec with { Policy = policy };

    private static ImmutableArray<T> Safe<T>(ImmutableArray<T> array) =>
        array.IsDefault ? ImmutableArray<T>.Empty : array;
}
