using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zigote.Http;

/// <summary>
///     What goes on the wire after the headers. A closed union, because the one question the
///     pipeline must never guess at is whether the body can be sent twice: retry and 401-replay
///     check <see cref="IsReplayable" /> rather than hoping.
/// </summary>
/// <remarks>
///     JSON is not a case of its own — <see cref="Json{T}" /> serializes eagerly into
///     <see cref="Bytes" />. That costs one serialization at build time and buys three things: the
///     spec stays a pure value (hashable, loggable, replayable), the request is length-known so it
///     goes out with a Content-Length instead of chunked, and nothing in the pipeline holds a
///     serializer.
/// </remarks>
public abstract record HttpBody
{
    private HttpBody() { }

    /// <summary>No body. The default for GET/HEAD/DELETE.</summary>
    public static HttpBody None { get; } = new NoBody();

    /// <summary>Whether the pipeline may send this body more than once.</summary>
    public abstract bool IsReplayable { get; }

    /// <summary>Bytes already in hand, with the content type they represent.</summary>
    public static HttpBody Bytes(ReadOnlyMemory<byte> bytes, string contentType = "application/octet-stream") =>
        new BytesBody(bytes, contentType);

    /// <summary>Text, UTF-8 encoded.</summary>
    public static HttpBody Text(string text, string contentType = "text/plain; charset=utf-8") =>
        new BytesBody(Encoding.UTF8.GetBytes(text), contentType);

    /// <summary>
    ///     <paramref name="value" /> serialized now, with a source-generated
    ///     <see cref="JsonTypeInfo{T}" />. Requiring the type info rather than a
    ///     <see cref="JsonSerializerOptions" /> is what keeps this assembly trim- and AOT-clean.
    /// </summary>
    public static HttpBody Json<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new BytesBody(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), "application/json; charset=utf-8");

    /// <summary>An <c>application/x-www-form-urlencoded</c> body.</summary>
    public static HttpBody Form(ImmutableArray<QueryParam> fields) => new FormBody(fields);

    /// <summary>
    ///     A <c>multipart/form-data</c> body — fields and file uploads together. Replayable exactly
    ///     when every part is, so a multipart of fields and byte files retries like any other value
    ///     and one stream part makes the whole request one-shot.
    /// </summary>
    public static HttpBody Multipart(params ReadOnlySpan<MultipartPart> parts) =>
        new MultipartBody([..parts]);

    /// <summary>
    ///     A stream, read once as it is sent. Not replayable — a request carrying one is never
    ///     retried and never replayed after a 401, and the caller owns disposing it.
    /// </summary>
    public static HttpBody Stream(Stream stream, string contentType = "application/octet-stream") =>
        new StreamBody(stream, contentType);

    /// <summary>Empty body.</summary>
    public sealed record NoBody : HttpBody
    {
        /// <inheritdoc />
        public override bool IsReplayable => true;
    }

    /// <summary>A length-known buffer. Covers bytes, text and JSON.</summary>
    public sealed record BytesBody(ReadOnlyMemory<byte> Content, string ContentType) : HttpBody
    {
        /// <inheritdoc />
        public override bool IsReplayable => true;
    }

    /// <summary>Form fields, encoded at send time.</summary>
    public sealed record FormBody(ImmutableArray<QueryParam> Fields) : HttpBody
    {
        /// <inheritdoc />
        public override bool IsReplayable => true;
    }

    /// <summary>A one-shot stream.</summary>
    public sealed record StreamBody(Stream Content, string ContentType) : HttpBody
    {
        /// <inheritdoc />
        public override bool IsReplayable => false;
    }

    /// <summary>Form fields and files, encoded as <c>multipart/form-data</c> at send time.</summary>
    public sealed record MultipartBody(ImmutableArray<MultipartPart> Parts) : HttpBody
    {
        /// <inheritdoc />
        public override bool IsReplayable
        {
            get
            {
                foreach (var part in Parts)
                    if (!part.Content.IsReplayable)
                        return false;
                return true;
            }
        }
    }
}

/// <summary>
///     One part of a multipart body. The content is itself an <see cref="HttpBody" /> — a field is a
///     text body, a file is a bytes or stream body — so the closed union carries replayability here
///     the same way it does everywhere else.
/// </summary>
/// <param name="Name">The form field name.</param>
/// <param name="Content">What the part carries. <see cref="HttpBody.None" /> parts are skipped.</param>
/// <param name="FileName">Set for file parts; drives the wire's <c>filename=</c> disposition.</param>
public sealed record MultipartPart(string Name, HttpBody Content, string? FileName = null)
{
    /// <summary>A plain form field.</summary>
    public static MultipartPart Field(string name, string value) =>
        new(name, HttpBody.Text(value));

    /// <summary>A file part from bytes in hand. Replayable, so the request stays retryable.</summary>
    public static MultipartPart File(
        string name, string fileName, ReadOnlyMemory<byte> bytes,
        string contentType = "application/octet-stream") =>
        new(name, HttpBody.Bytes(bytes, contentType), fileName);

    /// <summary>A file part streamed from <paramref name="stream" />. One-shot: the request is never retried.</summary>
    public static MultipartPart File(
        string name, string fileName, Stream stream,
        string contentType = "application/octet-stream") =>
        new(name, HttpBody.Stream(stream, contentType), fileName);
}
