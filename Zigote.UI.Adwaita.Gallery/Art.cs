using System.Text.Json.Serialization;
using Zigote.Http;
using Zigote.Http.Cache;

namespace AdwaitaGallery;

/// <summary>One picture and the artist who drew it.</summary>
internal readonly record struct ArtPiece(string Url, string Artist);

/// <summary>The feed's answer, as <c>nekos.best</c> spells it: <c>{"results":[{"url","artist_name"}]}</c>.</summary>
internal sealed record ArtFeed(ArtFeedItem[]? Results);

/// <inheritdoc cref="ArtFeed" />
internal sealed record ArtFeedItem(string? Url, string? ArtistName);

/// <summary>Source-generated contracts for the feed: no reflection for the AOT publish to trim away.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ArtFeed))]
internal sealed partial class ArtJson : JsonSerializerContext;

/// <summary>
///     The feed as an interface: <c>Zigote.Http.Generators</c> writes <c>NekosApiClient</c> at build
///     time, so the route, the query pair and the deadline are checked by the compiler rather than
///     assembled from strings at the call site.
/// </summary>
[HttpApi]
internal interface INekosApi
{
    /// <summary>A fresh random set from one category. The origin sends no cache headers, so this is never stored.</summary>
    [Get("{category}")]
    [Deadline(15)]
    Task<HttpResult<ArtFeed>>
        FeedAsync(string category, int amount, CancellationToken ct = default);
}

/// <summary>
///     Where the gallery's pictures come from: <c>nekos.best</c>, a free API of hand-drawn anime art
///     that credits every image to its artist. Nothing is redistributed here — the gallery holds
///     URLs, fetches them at runtime through one shared <see cref="HttpRunner" />, and shows the
///     credit on the picture.
/// </summary>
internal static class ArtSource
{
    /// <summary>Answers this run has served without the network — the status line's readout.</summary>
    public static int CacheHits;

    /// <summary>Answers this run took to the origin.</summary>
    public static int NetworkAnswers;

    /// <summary>
    ///     The one runner every picture and listing in this app goes through. The disk cache obeys
    ///     what the origin says: the images carry <c>max-age=691200</c> and an ETag, so they load
    ///     from disk across restarts and revalidate with a 304 after eight days; the feed listing
    ///     carries no cache headers and answers randomly, so it is honestly refetched — a page per
    ///     app run, not per scroll. The per-host gate keeps a fast fling from stampeding the CDN.
    /// </summary>
    public static readonly HttpRunner Http = new(
        new HttpRunnerOptions {
            BaseAddress = new Uri("https://nekos.best/api/v2/"),
            Cache = new FileCacheStore(FileCacheStore.DefaultDirectory),
            MaxConcurrencyPerHost = 6,
            OnLog = entry =>
            {
                if (entry.FromCache) Interlocked.Increment(ref CacheHits);
                else if (entry.Status is not null) Interlocked.Increment(ref NetworkAnswers);
            },
        }
    );

    private static readonly INekosApi Api = new NekosApiClient(runner: Http, json: ArtJson.Default);

    /// <summary>Pictures per feed page — one grid screenful, and one API call.</summary>
    public const int PageSize = 12;

    /// <summary>
    ///     Where the feed stops. A demo scrolling forever would keep calling a free API and keep
    ///     accumulating textures; a dozen pages is plenty to show paging working.
    /// </summary>
    public const int MaxPages = 12;

    /// <summary>
    ///     The carousel's fixed showcase. Fixed, not fetched: the cache is keyed by URL, so a
    ///     stable list is what makes the second run — and an offline one — paint straight from
    ///     disk. Every one comes from the API's SFW categories, and each artist writes in Latin
    ///     script so the credits survive the publish-time font subsetting this app turns on.
    /// </summary>
    public static readonly ArtPiece[] Showcase = [
        new(
            Url: "https://nekos.best/api/v2/neko/407f4272-8653-4d64-a222-6ecce753aaee.png",
            Artist: "STARFOX1015"
        ),
        new(
            Url: "https://nekos.best/api/v2/neko/2a751033-78da-4ff5-8d48-2177fb0b2af2.png",
            Artist: "Bling"
        ),
        new(
            Url: "https://nekos.best/api/v2/waifu/e8b562e6-a400-445b-8910-a7fedbc8843e.png",
            Artist: "Benkyousiro_0"
        ),
        new(
            Url: "https://nekos.best/api/v2/waifu/56c63765-9c41-4e28-bee6-7eb7981bb565.png",
            Artist: "Kou"
        ),
        new(
            Url: "https://nekos.best/api/v2/kitsune/e3180cfc-19d5-47ce-a3d7-d90a8f93068b.png",
            Artist: "Lamina"
        ),
        new(
            Url: "https://nekos.best/api/v2/kitsune/2fa354c9-c1b9-4ae3-8f18-c493b098357e.png",
            Artist: "Yagen"
        ),
        new(
            Url: "https://nekos.best/api/v2/husbando/5d01e511-79cd-40de-a183-82d3bde74440.png",
            Artist: "To___e"
        ),
        new(
            Url: "https://nekos.best/api/v2/husbando/695368f2-a140-4eb0-bde8-c4120d484f13.png",
            Artist: "Uniii"
        ),
    ];

    // The SFW categories, rotated so the feed does not read as one long variation on a theme.
    private static readonly string[] Categories = ["neko", "waifu", "kitsune", "husbando"];

    /// <summary>
    ///     One page of the feed, as a value: the pieces, or the <see cref="HttpError" /> that
    ///     explains why not — the page pattern-matches instead of catching. Runs entirely off the
    ///     UI thread; the runner's dedup and per-host gate mean a hundred of these in flight is a
    ///     hundred tasks, not a hundred sockets.
    /// </summary>
    public static async Task<HttpResult<ArtPiece[]>> FetchPageAsync(int page,
        CancellationToken ct = default)
    {
        // The endpoint answers with a fresh random set every call and says nothing about caching,
        // so nothing pretends otherwise: each page is fetched once per run (the grid keeps what it
        // got), and a restart asks again. The pictures those answers point at are the cacheable
        // part, and the runner's disk cache handles them by the origin's own rules.
        string category = Categories[page % Categories.Length];
        var result = await Api.FeedAsync(category: category, amount: PageSize, ct: ct)
            .ConfigureAwait(false);
        return result.Map(static feed => (feed.Results ?? [])
            .Where(static item => item.Url is not null)
            .Select(static item => new ArtPiece(
                    Url: item.Url!,
                    Artist: item.ArtistName ?? "Unknown artist"
                )
            )
            .ToArray()
        );
    }

    /// <summary>
    ///     The picture's original bytes, for the clipboard. The tile already fetched them, so this
    ///     is the disk cache answering — a copy costs a file read, not a round trip.
    /// </summary>
    public static ValueTask<HttpResult<byte[]>> BytesAsync(ArtPiece piece,
        CancellationToken ct = default) =>
        Http.BytesAsync(spec: HttpRequest.Get(piece.Url), ct: ct);

    /// <summary>
    ///     Saves the original to the Downloads folder. This is <see cref="HttpFile.DownloadAsync" />:
    ///     streamed to a <c>.part</c> beside the target and renamed, so a half-file is never
    ///     mistaken for a whole one, and a download cut off mid-way resumes with <c>If-Range</c>
    ///     from the bytes already on disk.
    /// </summary>
    /// <returns>Where it landed, or a line saying why not. Both null: it was canceled.</returns>
    public static async Task<(string? Path, string? Problem)> DownloadAsync(
        ArtPiece piece, IProgress<HttpProgress>? progress, CancellationToken ct = default)
    {
        // The URL came off the network: only its last segment is used, and only if it is a plain
        // file name — nothing in a feed answer gets to pick a directory.
        string name = Uri.TryCreate(
            uriString: piece.Url,
            uriKind: UriKind.Absolute,
            result: out var uri
        )
            ? Path.GetFileName(uri.AbsolutePath)
            : "";
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = "artwork.png";

        string folder = Path.Combine(
            path1: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            path2: "Downloads"
        );
        string path = Path.Combine(path1: folder, path2: name);
        try
        {
            Directory.CreateDirectory(folder);
            var result = await HttpFile.DownloadAsync(
                    runner: Http,
                    spec: HttpRequest.Get(piece.Url).Deadline(TimeSpan.FromMinutes(5)),
                    path: path,
                    progress: progress,
                    ct: ct
                )
                .ConfigureAwait(false);
            return result.Error switch {
                null => (path, null),
                HttpError.Canceled => (null, null),
                HttpError.Status status => (null, $"The server said {(int)status.Code}"),
                _ => (null, "Couldn't download — check the connection"),
            };
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, "Couldn't write to the Downloads folder");
        }
    }
}
