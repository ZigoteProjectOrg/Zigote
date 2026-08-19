using System.Globalization;
using System.Net;
using Zigote.Http.Transport;

namespace Zigote.Http;

/// <summary>
///     A seekable, read-only <see cref="Stream" /> over a remote resource, backed by range requests
///     and a small LRU of fetched blocks. Reading a 4 GB asset's footer costs two round trips
///     instead of a download.
/// </summary>
/// <remarks>
///     <para>
///         <b>It fails at open, or it works.</b> An origin without <c>Accept-Ranges</c>, or a
///         transport without <see cref="HttpCapabilities.Ranges" />, is
///         <see cref="PolicyFault.Unsupported" /> from <see cref="OpenAsync" /> — never a silent
///         degradation into downloading the whole thing.
///     </para>
///     <para>
///         <b>It never splices two versions together.</b> The validator seen at open travels with
///         every block fetch as <c>If-Range</c>. A resource that changes mid-read answers 200
///         instead of 206, and this throws <see cref="IOException" /> rather than handing back a
///         file that is half one version and half another.
///     </para>
/// </remarks>
public sealed class HttpFile : Stream
{
    private readonly int _blockSize;
    private readonly Dictionary<long, byte[]> _blocks = [];
    private readonly int _maxBlocks;
    private readonly LinkedList<long> _lru = new();
    private readonly HttpRunner _runner;
    private readonly HttpSpec _spec;
    private readonly string? _validator;

    private HttpFile(HttpRunner runner, HttpSpec spec, long length, string? validator, int blockSize, int maxBlocks)
    {
        _runner = runner;
        _spec = spec;
        Length = length;
        _validator = validator;
        _blockSize = blockSize;
        _maxBlocks = maxBlocks;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length { get; }

    /// <inheritdoc />
    public override long Position { get; set; }

    /// <summary>The <c>ETag</c> or <c>Last-Modified</c> pinned at open, sent as <c>If-Range</c> with every block.</summary>
    public string? Validator => _validator;

    /// <summary>
    ///     Opens <paramref name="spec" /> for random access.
    /// </summary>
    /// <param name="runner">The runner to fetch blocks with.</param>
    /// <param name="spec">Which resource. The verb is forced to GET.</param>
    /// <param name="blockSize">Bytes per fetch. One megabyte suits an asset CDN; smaller suits a metadata probe.</param>
    /// <param name="maxBlocks">How many blocks to keep. <c>blockSize * maxBlocks</c> is the memory this holds.</param>
    /// <param name="ct">Cancels the opening probe.</param>
    public static async ValueTask<HttpResult<HttpFile>> OpenAsync(
        HttpRunner runner,
        HttpSpec spec,
        int blockSize = 1 << 20,
        int maxBlocks = 8,
        CancellationToken ct = default)
    {
        if (!runner.Capabilities.Ranges)
            return HttpResult<HttpFile>.Fail(new HttpError.Policy(PolicyFault.Unsupported));

        // One byte, not a HEAD: a 206 proves ranges work, and Content-Range carries the total. Some
        // origins answer HEAD with a length they will not honour a range against.
        var probe = spec with { Verb = HttpVerb.Get };
        probe = probe.WithHeader("Range", "bytes=0-0").Cache(CacheMode.Bypass);

        var result = await runner.SendAsync(probe, ct).ConfigureAwait(false);
        if (!result.IsOk) return HttpResult<HttpFile>.Fail(result.Error);

        using var response = result.Value;
        if (response.Status != HttpStatusCode.PartialContent)
            return HttpResult<HttpFile>.Fail(new HttpError.Policy(PolicyFault.Unsupported));

        long? total = ParseTotal(response.Header("Content-Range"));
        if (total is null)
            return HttpResult<HttpFile>.Fail(new HttpError.Policy(PolicyFault.Unsupported));

        string? validator = response.Header("ETag") ?? response.Header("Last-Modified");
        return new HttpFile(runner, spec with { Verb = HttpVerb.Get }, total.Value, validator, blockSize, maxBlocks);
    }

    /// <summary>
    ///     Downloads <paramref name="spec" /> to <paramref name="path" />, resuming from whatever is
    ///     already on disk. Writes beside the target and renames, so a half-file is never mistaken
    ///     for a whole one.
    /// </summary>
    /// <returns>The number of bytes the file holds when this returns.</returns>
    public static async ValueTask<HttpResult<long>> DownloadAsync(
        HttpRunner runner,
        HttpSpec spec,
        string path,
        IProgress<HttpProgress>? progress = null,
        CancellationToken ct = default)
    {
        string partial = path + ".part";
        string sidecar = partial + ".validator";

        // Resume only what can be proven to be the same version. The validator captured when the
        // partial was first written travels back as If-Range: a origin that moved on answers 200
        // and the download starts over, instead of half of v1 being welded onto half of v2. A
        // partial with no recorded validator cannot be proven to be anything — start over.
        long have = 0;
        string? validator = null;
        if (File.Exists(partial))
        {
            validator = ReadSidecar(sidecar);
            if (validator is null) TryDelete(partial);
            else have = new FileInfo(partial).Length;
        }

        var request = (spec with { Verb = HttpVerb.Get }).Cache(CacheMode.Bypass).Streaming();
        if (have > 0 && runner.Capabilities.Ranges)
            request = request
                .WithHeader("Range", $"bytes={have}-")
                .WithHeader("If-Range", validator!);
        if (progress is not null) request = request.Progress(progress);

        var result = await runner.SendAsync(request, ct).ConfigureAwait(false);
        if (!result.IsOk) return HttpResult<long>.Fail(result.Error);

        using var response = result.Value;
        if (response.Status is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            return HttpResult<long>.Fail(new HttpError.Status(response.Status, []));

        // A 200 to a ranged request means the origin ignored the range or If-Range said the
        // resource changed: start over rather than appending the whole file onto the old part.
        bool append = response.Status == HttpStatusCode.PartialContent && have > 0;

        // Record this response's validator before writing a byte, so a crash mid-body leaves a
        // partial the next attempt can still prove something about.
        WriteSidecar(sidecar, response.Header("ETag") ?? response.Header("Last-Modified"));

        await using (var file = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None))
        {
            if (response.ContentStream is { } content)
                await content.CopyToAsync(file, ct).ConfigureAwait(false);
            else
                await file.WriteAsync(response.Body, ct).ConfigureAwait(false);
        }

        File.Move(partial, path, overwrite: true);
        TryDelete(sidecar);
        return new FileInfo(path).Length;
    }

    private static string? ReadSidecar(string sidecar)
    {
        try
        {
            string text = File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : "";
            return text.Length > 0 ? text : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteSidecar(string sidecar, string? validator)
    {
        try
        {
            if (validator is null) TryDelete(sidecar); // no validator: the next attempt starts over
            else File.WriteAllText(sidecar, validator);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken ct = default)
    {
        if (Position >= Length || buffer.IsEmpty) return 0;

        long block = Position / _blockSize;
        byte[] bytes = await BlockAsync(block, ct).ConfigureAwait(false);

        int offset = (int)(Position - (block * _blockSize));
        int available = Math.Min(bytes.Length - offset, (int)Math.Min(Length - Position, int.MaxValue));
        int count = Math.Min(buffer.Length, available);
        if (count <= 0) return 0;

        bytes.AsSpan(offset, count).CopyTo(buffer.Span);
        Position += count;
        return count;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        // Synchronous read over a network: correct, and slow by nature. Callers on the frame path
        // use the submit/poll queue instead; this exists so any Stream consumer works.
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return Position;
    }

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private async ValueTask<byte[]> BlockAsync(long block, CancellationToken ct)
    {
        if (_blocks.TryGetValue(block, out byte[]? cached))
        {
            _lru.Remove(block);
            _lru.AddFirst(block);
            return cached;
        }

        long start = block * _blockSize;
        long end = Math.Min(start + _blockSize, Length) - 1;

        var request = _spec
            .WithHeader("Range", $"bytes={start}-{end}")
            .Cache(CacheMode.Bypass);
        if (_validator is { Length: > 0 } validator)
            request = request.WithHeader("If-Range", validator);

        var result = await _runner.SendAsync(request, ct).ConfigureAwait(false);
        if (!result.IsOk) throw new HttpException(result.Error);

        using var response = result.Value;
        if (response.Status != HttpStatusCode.PartialContent)
            throw new IOException(
                $"Range request answered {(int)response.Status}: the resource changed while it was open.");

        byte[] bytes = response.Body.ToArray();
        _blocks[block] = bytes;
        _lru.AddFirst(block);
        while (_lru.Count > _maxBlocks && _lru.Last is { } last)
        {
            _blocks.Remove(last.Value);
            _lru.RemoveLast();
        }

        return bytes;
    }

    private static long? ParseTotal(string? contentRange)
    {
        // "bytes 0-0/12345", or "bytes 0-0/*" when the origin will not say.
        if (contentRange is null) return null;
        int slash = contentRange.LastIndexOf('/');
        if (slash < 0) return null;
        return long.TryParse(contentRange.AsSpan(slash + 1), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long total)
            ? total
            : null;
    }
}
