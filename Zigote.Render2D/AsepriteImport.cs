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
            throw new FormatException(
                message: $"Aseprite JSON is not valid JSON: {e.Message}",
                innerException: e
            );
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(propertyName: "frames", value: out var framesEl))
                throw new FormatException("Aseprite JSON has no \"frames\" property.");

            var frames = new List<AsepriteFrame>();
            switch (framesEl.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in framesEl.EnumerateObject())
                        frames.Add(ReadFrame(name: prop.Name, el: prop.Value));
                    break;

                case JsonValueKind.Array:
                {
                    int index = 0;
                    foreach (var el in framesEl.EnumerateArray())
                    {
                        string name = el.ValueKind == JsonValueKind.Object
                                      && el.TryGetProperty(
                                          propertyName: "filename",
                                          value: out var filename
                                      )
                                      && filename.ValueKind == JsonValueKind.String
                            ? filename.GetString()!
                            : $"frame {index}";
                        frames.Add(ReadFrame(name: name, el: el));
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
            if (root.TryGetProperty(propertyName: "meta", value: out var meta) &&
                meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty(propertyName: "size", value: out var size) &&
                    size.ValueKind == JsonValueKind.Object)
                {
                    sheetW = ReadInt(obj: size, property: "w", fallback: 0);
                    sheetH = ReadInt(obj: size, property: "h", fallback: 0);
                }

                if (meta.TryGetProperty(propertyName: "frameTags", value: out var tagsEl) &&
                    tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tagsEl.EnumerateArray())
                    {
                        if (tag.ValueKind != JsonValueKind.Object) continue;
                        string name = tag.TryGetProperty(propertyName: "name", value: out var n) &&
                                      n.ValueKind == JsonValueKind.String
                            ? n.GetString()!
                            : "";
                        string direction =
                            tag.TryGetProperty(propertyName: "direction", value: out var d) &&
                            d.ValueKind == JsonValueKind.String
                                ? d.GetString()!.ToLowerInvariant()
                                : "forward";
                        tags.Add(
                            new AsepriteTag(
                                Name: name,
                                From: ReadInt(obj: tag, property: "from", fallback: 0),
                                To: ReadInt(obj: tag, property: "to", fallback: 0),
                                Direction: direction
                            )
                        );
                    }
                }
            }

            return new AsepriteDocument(
                frames: [.. frames],
                tags: [.. tags],
                sheetWidth: sheetW,
                sheetHeight: sheetH
            );
        }
    }

    public static List<SpriteClip> ToClips(AsepriteDocument document, SpriteTexture texture) =>
        ToClips(document: document, textureWidth: texture.Width, textureHeight: texture.Height);

    /// <summary>
    ///     Headless overload: normalizes pixel rects against the texture size exactly like
    ///     SpriteSheet.GridFrames.
    /// </summary>
    public static List<SpriteClip> ToClips(AsepriteDocument document, int textureWidth,
        int textureHeight)
    {
        var source = document.Frames;
        float invW = 1f / textureWidth;
        float invH = 1f / textureHeight;
        var frames = new SpriteFrame[source.Count];
        float[] durations = new float[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            var f = source[i];
            frames[i] = new SpriteFrame(
                U0: f.X * invW,
                V0: f.Y * invH,
                U1: (f.X + f.W) * invW,
                V1: (f.Y + f.H) * invH,
                PixelWidth: f.W,
                PixelHeight: f.H
            );
            durations[i] = f.DurationSeconds;
        }

        var clips = new List<SpriteClip>();
        if (document.Tags.Count == 0)
        {
            clips.Add(new SpriteClip(name: "default", frames: frames, durations: durations));
            return clips;
        }

        foreach (var tag in document.Tags)
        {
            if (frames.Length == 0) break;
            int from = Math.Clamp(value: tag.From, min: 0, max: frames.Length - 1);
            int to = Math.Clamp(value: tag.To, min: 0, max: frames.Length - 1);
            if (to < from) continue;

            int count = to - from + 1;
            var clipFrames = new SpriteFrame[count];
            float[] clipDurations = new float[count];
            bool reversed = tag.Direction == "reverse";
            for (int i = 0; i < count; i++)
            {
                int src = reversed ? to - i : from + i;
                clipFrames[i] = frames[src];
                clipDurations[i] = durations[src];
            }

            // "pingpong" also matches Aseprite 1.3's "pingpong_reverse".
            var loop = tag.Direction.StartsWith(
                value: "pingpong",
                comparisonType: StringComparison.Ordinal
            )
                ? SpriteLoopMode.PingPong
                : SpriteLoopMode.Loop;
            clips.Add(
                new SpriteClip(
                    name: tag.Name,
                    frames: clipFrames,
                    durations: clipDurations,
                    loop: loop
                )
            );
        }

        return clips;
    }

    private static AsepriteFrame ReadFrame(string name, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty(propertyName: "frame", value: out var rect) ||
            rect.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Aseprite frame \"{name}\" is missing its \"frame\" rect.");

        return new AsepriteFrame(
            Name: name,
            X: RequireInt(obj: rect, property: "x", frameName: name),
            Y: RequireInt(obj: rect, property: "y", frameName: name),
            W: RequireInt(obj: rect, property: "w", frameName: name),
            H: RequireInt(obj: rect, property: "h", frameName: name),
            DurationSeconds: ReadInt(obj: el, property: "duration", fallback: 100) / 1000f
        );
    }

    private static int RequireInt(JsonElement obj, string property, string frameName)
    {
        if (!obj.TryGetProperty(propertyName: property, value: out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException(
                $"Aseprite frame \"{frameName}\" rect is missing \"{property}\"."
            );
        }

        return value.GetInt32();
    }

    private static int ReadInt(JsonElement obj, string property, int fallback)
    {
        return obj.TryGetProperty(propertyName: property, value: out var value) &&
               value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;
    }
}
