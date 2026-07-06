using System.Text.Json;

namespace Zigote.Render2D;

/// <summary>
///     A frame rect in sheet pixels (y from the top row, like <see cref="SpriteFrame" /> V) + its
///     duration.
/// </summary>
public readonly record struct AsepriteFrame(
    string Name,
    int X,
    int Y,
    int W,
    int H,
    float DurationSeconds);

/// <summary>
///     A frameTags entry; Direction is the lowercased Aseprite value
///     (forward/reverse/pingpong/…).
/// </summary>
public readonly record struct AsepriteTag(string Name, int From, int To, string Direction);

public sealed class AsepriteDocument
{
    private readonly AsepriteFrame[] _frames;
    private readonly AsepriteTag[] _tags;

    internal AsepriteDocument(AsepriteFrame[] frames, AsepriteTag[] tags, int sheetWidth,
        int sheetHeight)
    {
        _frames = frames;
        _tags = tags;
        SheetWidth = sheetWidth;
        SheetHeight = sheetHeight;
    }

    public IReadOnlyList<AsepriteFrame> Frames => _frames;
    public IReadOnlyList<AsepriteTag> Tags => _tags;

    /// <summary>Sheet size from meta.size; 0 when the export omitted it.</summary>
    public int SheetWidth { get; }

    public int SheetHeight { get; }
}

/// <summary>
///     Parses Aseprite's "Export Sprite Sheet → JSON Data" output (both the hash form, frames keyed
///     by name, and the array form with filename entries) into clips. Import-time tooling — throws
///     <see cref="FormatException" /> on malformed input rather than limping along.
/// </summary>
public static class AsepriteImport
{
    public static AsepriteDocument Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new FormatException($"Aseprite JSON is not valid JSON: {e.Message}", e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("frames", out var framesEl))
                throw new FormatException("Aseprite JSON has no \"frames\" property.");

            var frames = new List<AsepriteFrame>();
            switch (framesEl.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in framesEl.EnumerateObject())
                        frames.Add(ReadFrame(prop.Name, prop.Value));
                    break;

                case JsonValueKind.Array:
                {
                    var index = 0;
                    foreach (var el in framesEl.EnumerateArray())
                    {
                        var name = el.ValueKind == JsonValueKind.Object
                                   && el.TryGetProperty("filename", out var filename)
                                   && filename.ValueKind == JsonValueKind.String
                            ? filename.GetString()!
                            : $"frame {index}";
                        frames.Add(ReadFrame(name, el));
                        index++;
                    }

                    break;
                }

                default:
                    throw new FormatException(
                        "Aseprite \"frames\" must be an object (hash) or an array."
                    );
            }

            var tags = new List<AsepriteTag>();
            int sheetW = 0, sheetH = 0;
            if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty("size", out var size) &&
                    size.ValueKind == JsonValueKind.Object)
                {
                    sheetW = ReadInt(size, "w", 0);
                    sheetH = ReadInt(size, "h", 0);
                }

                if (meta.TryGetProperty("frameTags", out var tagsEl) &&
                    tagsEl.ValueKind == JsonValueKind.Array)
                    foreach (var tag in tagsEl.EnumerateArray())
                    {
                        if (tag.ValueKind != JsonValueKind.Object) continue;
                        var name = tag.TryGetProperty("name", out var n) &&
                                   n.ValueKind == JsonValueKind.String
                            ? n.GetString()!
                            : "";
                        var direction =
                            tag.TryGetProperty("direction", out var d) &&
                            d.ValueKind == JsonValueKind.String
                                ? d.GetString()!.ToLowerInvariant()
                                : "forward";
                        tags.Add(
                            new AsepriteTag(
                                name,
                                ReadInt(tag, "from", 0),
                                ReadInt(tag, "to", 0),
                                direction
                            )
                        );
                    }
            }

            return new AsepriteDocument(
                [.. frames],
                [.. tags],
                sheetW,
                sheetH
            );
        }
    }

    public static List<SpriteClip> ToClips(AsepriteDocument document, SpriteTexture texture)
    {
        return ToClips(document, texture.Width, texture.Height);
    }

    /// <summary>
    ///     Headless overload: normalizes pixel rects against the texture size exactly like
    ///     SpriteSheet.GridFrames.
    /// </summary>
    public static List<SpriteClip> ToClips(AsepriteDocument document, int textureWidth,
        int textureHeight)
    {
        var source = document.Frames;
        var invW = 1f / textureWidth;
        var invH = 1f / textureHeight;
        var frames = new SpriteFrame[source.Count];
        var durations = new float[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var f = source[i];
            frames[i] = new SpriteFrame(
                f.X * invW,
                f.Y * invH,
                (f.X + f.W) * invW,
                (f.Y + f.H) * invH,
                f.W,
                f.H
            );
            durations[i] = f.DurationSeconds;
        }

        var clips = new List<SpriteClip>();
        if (document.Tags.Count == 0)
        {
            clips.Add(new SpriteClip("default", frames, durations));
            return clips;
        }

        foreach (var tag in document.Tags)
        {
            if (frames.Length == 0) break;
            var from = Math.Clamp(tag.From, 0, frames.Length - 1);
            var to = Math.Clamp(tag.To, 0, frames.Length - 1);
            if (to < from) continue;

            var count = to - from + 1;
            var clipFrames = new SpriteFrame[count];
            var clipDurations = new float[count];
            var reversed = tag.Direction == "reverse";
            for (var i = 0; i < count; i++)
            {
                var src = reversed ? to - i : from + i;
                clipFrames[i] = frames[src];
                clipDurations[i] = durations[src];
            }

            // "pingpong" also matches Aseprite 1.3's "pingpong_reverse".
            var loop = tag.Direction.StartsWith("pingpong", StringComparison.Ordinal)
                ? SpriteLoopMode.PingPong
                : SpriteLoopMode.Loop;
            clips.Add(
                new SpriteClip(
                    tag.Name,
                    clipFrames,
                    clipDurations,
                    loop
                )
            );
        }

        return clips;
    }

    private static AsepriteFrame ReadFrame(string name, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty("frame", out var rect) || rect.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Aseprite frame \"{name}\" is missing its \"frame\" rect.");

        return new AsepriteFrame(
            name,
            RequireInt(rect, "x", name),
            RequireInt(rect, "y", name),
            RequireInt(rect, "w", name),
            RequireInt(rect, "h", name),
            ReadInt(el, "duration", 100) / 1000f
        );
    }

    private static int RequireInt(JsonElement obj, string property, string frameName)
    {
        if (!obj.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new FormatException(
                $"Aseprite frame \"{frameName}\" rect is missing \"{property}\"."
            );
        return value.GetInt32();
    }

    private static int ReadInt(JsonElement obj, string property, int fallback)
    {
        return obj.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;
    }
}