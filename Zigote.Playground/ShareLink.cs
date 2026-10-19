using System.IO.Compression;
using System.Text;

namespace Playground;

/// <summary>An example that arrived in a link: its code, preview height and the page it came from.</summary>
internal sealed record SharedExample(string Source, int Height, string? From);

/// <summary>
///     Shareable example links. The link carries the code itself — deflated, base64url — so it
///     needs no server and works offline:
///     <c>zigote://playground/example?v=1&amp;h=320&amp;from=doc:Layout&amp;code=…</c>.
///     <c>from</c> is a page link (<c>doc:Title</c> or <c>type:Full.Name</c>) for "shared from".
/// </summary>
internal static class ShareLink
{
    public const string Scheme = "zigote://playground/";
    private const string Prefix = Scheme + "example?";

    // A link decodes to at most this much code: the payload is untrusted, and deflate can expand
    // a few KB into gigabytes.
    private const int MaxSourceBytes = 256 * 1024;

    public static string Create(string source, int height, string? from)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize))
            deflate.Write(Encoding.UTF8.GetBytes(source));
        string code = Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string origin = from is null ? "" : $"&from={Uri.EscapeDataString(from)}";
        return $"{Prefix}v=1&h={height}{origin}&code={code}";
    }

    /// <summary>The example in <paramref name="link" />, or null if it is not a (valid, v1) playground link.</summary>
    public static SharedExample? Parse(string link)
    {
        link = link.Trim();
        if (!link.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in link[Prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) query[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        if (query.GetValueOrDefault("v") != "1" || !query.TryGetValue("code", out string? code)) return null;
        int height = int.TryParse(query.GetValueOrDefault("h"), out int h) ? Math.Clamp(h, 80, 1200) : 320;
        string? from = query.GetValueOrDefault("from") is { Length: > 0 } f && (f.StartsWith("doc:") || f.StartsWith("type:")) ? f : null;
        return Decode(code) is { } source ? new SharedExample(source, height, from) : null;
    }

    private static string? Decode(string code)
    {
        try
        {
            string b64 = code.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
            using var deflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
            var buffer = new byte[MaxSourceBytes + 1];
            int total = 0, read;
            while (total < buffer.Length && (read = deflate.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            if (total > MaxSourceBytes) return null;
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, total);
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or DecoderFallbackException)
        {
            return null;
        }
    }
}
