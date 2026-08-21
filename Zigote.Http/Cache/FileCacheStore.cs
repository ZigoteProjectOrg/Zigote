using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Zigote.Http.Cache;

/// <summary>
///     A disk store: one file per entry, named by a hash of the key, written to a temp name and
///     renamed into place so a reader never sees half an entry and two processes racing on one key
///     both end up with a whole one.
/// </summary>
/// <remarks>
///     <para>
///         The format is a small binary record rather than JSON: it is written on every miss and read
///         on every hit, and a body is bytes, not text. Version-tagged, and a record that fails to
///         parse is treated as a miss and deleted — a cache that can fail a request is worse than no
///         cache.
///     </para>
///     <para>
///         Eviction is a size-budgeted sweep, run at most once a minute and only after a write:
///         oldest-accessed files go first. That is coarse compared to the memory store's exact LRU,
///         and it is the right trade for a directory whose true size is only knowable by stat-ing
///         every file.
///     </para>
/// </remarks>
public sealed class FileCacheStore : IHttpCacheStore
{
    private const int Magic = 0x5A48_4331; // "ZHC1"
    private readonly long _budgetBytes;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private long _lastSweepTicks;

    /// <summary>A store under <paramref name="directory" />, created on first write.</summary>
    /// <param name="directory">Where entries live. One directory per app, not per runner.</param>
    /// <param name="budgetBytes">Total bytes of bodies to keep. The sweep runs when a write pushes past this.</param>
    /// <param name="time">Clock, injected so eviction tests are deterministic.</param>
    public FileCacheStore(string directory, long budgetBytes = 256L * 1024 * 1024, TimeProvider? time = null)
    {
        _directory = directory;
        _budgetBytes = budgetBytes;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The default location: a Zigote-owned folder under the user's cache directory.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } local
            ? local
            : Path.GetTempPath(),
        "Zigote", "http-cache");

    /// <inheritdoc />
    public async ValueTask<CachedResponse?> GetAsync(string key, CancellationToken ct = default)
    {
        string path = PathFor(key);
        // A miss is the common case on a cold feed; probing first keeps it from being an
        // exception per request (the catch below still covers the delete race).
        if (!File.Exists(path)) return null;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var entry = Deserialize(bytes);
            if (entry is null)
            {
                Delete(path);
                return null;
            }

            // Access time drives the sweep's eviction order. Best-effort: some filesystems refuse.
            try { File.SetLastAccessTimeUtc(path, _time.GetUtcNow().UtcDateTime); }
            catch (IOException) { }

            return entry;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, CachedResponse entry, CancellationToken ct = default)
    {
        string path = PathFor(key);
        string partial = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.part";
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(partial, Serialize(entry), ct).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);
            Sweep();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unwritable cache is a slow cache, not a failed request.
            Delete(partial);
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        Delete(PathFor(key));
        return ValueTask.CompletedTask;
    }

    /// <summary>Delete every entry. Equivalent to removing the directory, and safe while requests run.</summary>
    public void Clear()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (string file in Directory.EnumerateFiles(_directory, "*.zhc")) Delete(file);
    }

    private void Sweep()
    {
        // At most once a minute: stat-ing the whole directory on every miss would cost more than
        // the entries save.
        long now = _time.GetUtcNow().UtcTicks;
        long last = Interlocked.Read(ref _lastSweepTicks);
        if (now - last < TimeSpan.TicksPerMinute) return;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last) return;

        try
        {
            var files = new DirectoryInfo(_directory).GetFiles("*.zhc");
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= _budgetBytes) return;

            Array.Sort(files, static (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
            foreach (var f in files)
            {
                if (total <= _budgetBytes) break;
                total -= f.Length;
                Delete(f.FullName);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string PathFor(string key)
    {
        // Content-addressed by key: a URL is not a filename (length limits, '/', case-insensitive
        // volumes), and half a SHA-256 is far past collision territory for a per-machine cache.
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, $"{Convert.ToHexStringLower(digest.AsSpan(0, 16))}.zhc");
    }

    private static byte[] Serialize(CachedResponse entry)
    {
        using var buffer = new MemoryStream(entry.Body.Length + 256);
        using (var w = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write((int)entry.Status);
            w.Write(entry.StoredAt.UtcTicks);
            w.Write(entry.InitialAgeSeconds);
            w.Write(entry.VaryKey);
            w.Write(entry.Headers.Length);
            foreach (var h in entry.Headers)
            {
                w.Write(h.Name);
                w.Write(h.Value);
            }

            w.Write(entry.Body.Length);
            w.Write(entry.Body);
        }

        return buffer.ToArray();
    }

    private static CachedResponse? Deserialize(byte[] bytes)
    {
        try
        {
            using var buffer = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(buffer, Encoding.UTF8);
            if (r.ReadInt32() != Magic) return null;

            var status = (HttpStatusCode)r.ReadInt32();
            var storedAt = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);
            long initialAge = r.ReadInt64();
            string varyKey = r.ReadString();

            int headerCount = r.ReadInt32();
            if (headerCount is < 0 or > 1024) return null;
            var headers = ImmutableArray.CreateBuilder<HeaderPair>(headerCount);
            for (int i = 0; i < headerCount; i++) headers.Add(new HeaderPair(r.ReadString(), r.ReadString()));

            int bodyLength = r.ReadInt32();
            if (bodyLength < 0) return null;
            byte[] body = r.ReadBytes(bodyLength);
            if (body.Length != bodyLength) return null;

            return new CachedResponse(status, headers.ToImmutable(), body, storedAt, initialAge, varyKey);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
