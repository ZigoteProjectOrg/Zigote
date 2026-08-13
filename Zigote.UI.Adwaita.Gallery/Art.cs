using System.Text.Json;
using Zigote.UI.Net;

namespace AdwaitaGallery;

/// <summary>One picture and the artist who drew it.</summary>
internal readonly record struct ArtPiece(string Url, string Artist);

/// <summary>
///     Where the gallery's pictures come from: <c>nekos.best</c>, a free API of hand-drawn anime art
///     that credits every image to its artist. Nothing is redistributed here — the gallery holds
///     URLs, fetches them at runtime through <see cref="NetworkCache" />, and shows the credit on
///     the picture.
/// </summary>
internal static class ArtSource
{
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
    ///     One page of the feed. Runs entirely off the UI thread and comes back on the caller's
    ///     thread — <see cref="NetworkCache" /> gates and coalesces the request, so a hundred of
    ///     these in flight is a hundred tasks, not a hundred sockets.
    /// </summary>
    public static async Task<ArtPiece[]> FetchPageAsync(int page, CancellationToken ct = default)
    {
        string category = Categories[page % Categories.Length];
        string url = $"https://nekos.best/api/v2/{category}?amount={PageSize}";

        // Keyed by page, not by URL: this endpoint answers with a fresh random set every call, so
        // URL-keyed caching would pin page 0's pictures onto every page after it. With the page in
        // the key, each page hits the API exactly once per machine and is read from disk forever
        // after — scrolling back up, or restarting, costs the API nothing.
        byte[] json = await NetworkCache.FetchAsync(
                url: url,
                ct: ct,
                cacheKey: $"nekos:{category}:page{page}"
            )
            .ConfigureAwait(false);
        return Parse(json);
    }

    private static ArtPiece[] Parse(byte[] json)
    {
        // JsonDocument, not a deserialized model: one shape, two fields, and no reflection for the
        // AOT publish to trim away.
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(
                propertyName: "results",
                value: out var results
            )) return [];

        var pieces = new List<ArtPiece>(results.GetArrayLength());
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty(propertyName: "url", value: out var url) ||
                url.GetString() is not { } link)
                continue;
            string artist = result.TryGetProperty(propertyName: "artist_name", value: out var name)
                ? name.GetString() ?? "Unknown artist"
                : "Unknown artist";
            pieces.Add(new ArtPiece(Url: link, Artist: artist));
        }

        return [.. pieces];
    }
}
