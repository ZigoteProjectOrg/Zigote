using System.Buffers;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zigote.Http;

/// <summary>
///     What came back. Owns its body buffer, which is why it is disposable — dispose returns a
///     pooled array, or closes the stream when the request asked for
///     <see cref="RequestPolicy.Streaming" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Ownership, spelled out</b> (design doc §14.4): the caller who receives a response owns
///         it and disposes it. A buffered response's <see cref="Body" /> is valid until then, so copy
///         what outlives the <c>using</c>. A streaming response hands over
///         <see cref="ContentStream" />: disposing the response disposes the stream, and if the
///         deadline fires mid-read the stream throws <see cref="OperationCanceledException" /> —
///         that one case is an exception rather than an <see cref="HttpError" />, because by then
///         the caller, not this library, is doing the reading.
///     </para>
///     <para>
///         Responses handed out by the cache and the dedup layers are <see cref="Detach">detached</see>:
///         they share one immutable body array and disposing them is a no-op, so N callers sharing
///         one in-flight GET cannot double-free anything.
///     </para>
/// </remarks>
public sealed class HttpResponse : IDisposable
{
    private readonly byte[] _buffer;
    private readonly bool _pooled;
    private bool _disposed;

    private HttpResponse(
        HttpStatusCode status,
        ImmutableArray<HeaderPair> headers,
        byte[] buffer,
        int length,
        bool pooled,
        Stream? contentStream,
        bool fromCache)
    {
        Status = status;
        Headers = headers;
        _buffer = buffer;
        BodyLength = length;
        _pooled = pooled;
        ContentStream = contentStream;
        FromCache = fromCache;
    }

    /// <summary>The status line.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Response headers in wire order, content headers included.</summary>
    public ImmutableArray<HeaderPair> Headers { get; }

    /// <summary>Bytes in <see cref="Body" />.</summary>
    public int BodyLength { get; }

    /// <summary>
    ///     The buffered body. Empty for a streaming response — read <see cref="ContentStream" />
    ///     instead. Valid until <see cref="Dispose" />.
    /// </summary>
    public ReadOnlyMemory<byte> Body => new(_buffer, 0, BodyLength);

    /// <summary>The open body stream, non-null only when the request set <see cref="RequestPolicy.Streaming" />.</summary>
    public Stream? ContentStream { get; }

    /// <summary>Whether this was served from the response cache rather than the origin.</summary>
    public bool FromCache { get; }

    /// <summary>2xx.</summary>
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ContentStream?.Dispose();
        if (_pooled) ArrayPool<byte>.Shared.Return(_buffer);
    }

    /// <summary>A buffered response over a pooled array. Dispose returns it to the pool.</summary>
    public static HttpResponse FromPooled(
        HttpStatusCode status, ImmutableArray<HeaderPair> headers, byte[] rented, int length) =>
        new(status, headers, rented, length, pooled: true, contentStream: null, fromCache: false);

    /// <summary>A buffered response over an array this library did not rent. Dispose is a no-op.</summary>
    public static HttpResponse FromBytes(
        HttpStatusCode status, ImmutableArray<HeaderPair> headers, byte[] body, bool fromCache = false) =>
        new(status, headers, body, body.Length, pooled: false, contentStream: null, fromCache: fromCache);

    /// <summary>A response whose body is still on the wire.</summary>
    public static HttpResponse FromStream(
        HttpStatusCode status, ImmutableArray<HeaderPair> headers, Stream content) =>
        new(status, headers, [], 0, pooled: false, contentStream: content, fromCache: false);

    /// <summary>
    ///     A copy that owns nothing poolable, so it can be shared, stored and disposed by anyone.
    ///     The cost is one array copy, paid once where a response is about to have several owners.
    /// </summary>
    public HttpResponse Detach(bool fromCache = false) =>
        _pooled || ContentStream is not null
            ? FromBytes(Status, Headers, Body.ToArray(), fromCache)
            : fromCache == FromCache
                ? this
                : FromBytes(Status, Headers, _buffer, fromCache);

    /// <summary>The first value of <paramref name="name" />, or null. Case-insensitive, as HTTP defines.</summary>
    public string? Header(string name)
    {
        foreach (var h in Headers)
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                return h.Value;
        return null;
    }

    /// <summary>The body as UTF-8 text.</summary>
    public string Text() => Encoding.UTF8.GetString(Body.Span);

    /// <summary>
    ///     The body deserialized with a source-generated contract. A malformed body is
    ///     <see cref="HttpError.Decode" />, not an exception.
    /// </summary>
    public HttpResult<T> Json<T>(JsonTypeInfo<T> typeInfo)
    {
        try
        {
            var value = JsonSerializer.Deserialize(Body.Span, typeInfo);
            return value is null
                ? HttpResult<T>.Fail(new HttpError.Decode(typeof(T), new JsonException("body was null")))
                : HttpResult<T>.Ok(value);
        }
        catch (JsonException e)
        {
            return HttpResult<T>.Fail(new HttpError.Decode(typeof(T), e));
        }
    }
}
