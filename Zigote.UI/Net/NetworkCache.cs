using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Zigote.UI.Net;

/// <summary>
///     Fetches a URL over HTTP once and keeps the bytes on disk. Built for the two things a UI
///     downloads — pictures and the JSON that lists them — and shaped for
///     <c>Image.LoadAsync</c>'s <c>fetch</c> delegate, which is the main use:
///     <code>image.LoadAsync(ct => NetworkCache.FetchAsync(url, ct), maxDim: 512);</code>
/// </summary>
/// <remarks>
///     <para>
///         <b>Built for thousands of calls.</b> Three properties make a grid that asks for ten
///         thousand pictures behave: requests for one URL are <b>coalesced</b> into a single fetch,
///         concurrency is <b>gated</b> at <see cref="MaxConcurrentFetches" /> so a wide fan-out
///         queues instead of opening ten thousand sockets and holding ten thousand buffers, and a
///         completed URL is served from <b>disk</b> for the rest of the machine's life. A queued
///         call costs a task, not a thread — nothing here blocks, and nothing here touches the UI
///         thread.
///     </para>
///     <para>
///         <b>Nothing expires.</b> That is the right trade for URLs that name immutable content
///         (every image CDN) and the wrong one for a URL that answers differently each request —
///         for those, pass an explicit <c>cacheKey</c> that says which answer you are keeping, the
///         way a paged feed keys by page number. Delete <see cref="CacheDirectory" /> to clear the
///         cache; there is no API for it because <c>rm -rf</c> is one.
///     </para>
///     <para>
///         This caches <b>encoded bytes</b>, not decoded textures. The decode still runs per load,
///         off the frame loop, inside <c>Image.LoadAsync</c>.
///     </para>
/// </remarks>
public static class NetworkCache
{
    /// <summary>
    ///     How many fetches run at once. Browsers settle around six per host for the same reason:
    ///     past that a wider fan-out trades latency for nothing. The gate also bounds how many
    ///     encoded bodies are in memory at once, which is what keeps a ten-thousand-item queue
    ///     costing megabytes instead of gigabytes.
    /// </summary>
    public const int MaxConcurrentFetches = 6;

    /// <summary>
    ///     Sent with every request, and not decoration: plenty of CDNs answer a request carrying no
    ///     User-Agent with a flat 403, which turns an otherwise healthy cache into what looks like a
    ///     total network failure. <see cref="HttpClient" /> sends none by default, so this does.
    /// </summary>
    public const string UserAgent = "Zigote/1.0 (+https://github.com/ZigoteProjectOrg/Zigote)";

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(MaxConcurrentFetches, MaxConcurrentFetches);

    private static readonly ConcurrentDictionary<string, Task<byte[]>> InFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     Where cached bodies live; created on the first write. The default sits under the system
    ///     temp directory, so the OS reclaims it — point it somewhere the app owns to survive a
    ///     reboot. Set it before the first fetch.
    /// </summary>
    public static string CacheDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "zigote-network-cache");

    /// <summary>
    ///     The bytes at <paramref name="url" />: from disk when they are already there, from the
    ///     network otherwise, and from whichever fetch is already running when one is.
    ///     <para>
    ///         <paramref name="cacheKey" /> overrides what the entry is filed under. Leave it null
    ///         for a URL that names its content; pass one when the same URL yields different bodies
    ///         and you know which is which (<c>$"feed:page{n}"</c>).
    ///     </para>
    ///     <para>
    ///         Throws whatever <see cref="HttpClient" /> throws on a failed request — no network, a
    ///         404, a timeout. <see cref="OperationCanceledException" /> when
    ///         <paramref name="ct" /> fires, which abandons this caller's wait and leaves the shared
    ///         fetch running for everyone else.
    ///     </para>
    /// </summary>
    public static async Task<byte[]> FetchAsync(string url, CancellationToken ct = default,
        string? cacheKey = null)
    {
        var key = cacheKey ?? url;

        // Coalesced: a screenful of tiles asking for one URL share one fetch. The shared task runs
        // untied to any caller's token — one caller walking away must not cancel it for the rest —
        // and WaitAsync gives each caller its own cancellation without touching the work.
        var shared = InFlight.GetOrAdd(key, _ => Observed(FetchUncachedAsync(url, key)));
        return await shared.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Whether the entry is already on disk — no request, no I/O wait, no exception. For a
    ///     "cached / downloaded" readout, or to decide whether a placeholder is worth showing.
    /// </summary>
    public static bool IsCached(string url, string? cacheKey = null)
    {
        return File.Exists(CachePath(cacheKey ?? url));
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private static async Task<byte[]> FetchUncachedAsync(string url, string key)
    {
        var path = CachePath(key);
        try
        {
            // The gate covers the disk read as well as the request. A thousand cached tiles read at
            // once is a thousand multi-megabyte buffers alive at once — bounding both paths is what
            // makes "everything is already cached" the cheap case rather than the expensive one.
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (File.Exists(path))
                    try
                    {
                        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Vanished or locked between the check and the read: fall through and refetch.
                    }

                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                await StoreAsync(path, bytes).ConfigureAwait(false);
                return bytes;
            }
            finally
            {
                Gate.Release();
            }
        }
        finally
        {
            // Dropped on completion, not on success: a failed fetch must be retryable, and by the
            // time this runs the bytes are on disk anyway, so the next caller reads them from there.
            InFlight.TryRemove(key, out _);
        }
    }

    private static async Task StoreAsync(string path, byte[] bytes)
    {
        // Write beside the target and rename: a reader never observes a half-written file, and two
        // fetches of one URL racing each other both land on identical content.
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var partial = $"{path}.{Environment.CurrentManagedThreadId}.part";
            await File.WriteAllBytesAsync(partial, bytes).ConfigureAwait(false);
            File.Move(partial, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unwritable cache is a slow cache, not a failed fetch: the bytes are already in hand.
        }
    }

    private static Task<byte[]> Observed(Task<byte[]> task)
    {
        // Every caller can cancel before a shared fetch faults, and an exception no one ever awaits
        // resurfaces as TaskScheduler.UnobservedTaskException at the next GC. Look at it here.
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        return task;
    }

    private static string CachePath(string key)
    {
        // Hashed, not sanitised: a URL is not a filename (length limits, '/', case-insensitive
        // volumes), and half a SHA-256 is far past collision territory for a per-machine cache.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(CacheDirectory, $"{Convert.ToHexStringLower(digest.AsSpan(0, 16))}.bin");
    }
}
