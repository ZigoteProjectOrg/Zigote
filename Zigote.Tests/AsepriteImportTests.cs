using System.Text;
using Xunit;
using Zigote.Render2D;

namespace Zigote.Tests;

public class AsepriteImportTests
{
    private const string HashJson = """
                                    {
                                      "frames": {
                                        "hero 0.aseprite": { "frame": { "x": 0, "y": 0, "w": 16, "h": 16 }, "rotated": false, "trimmed": false, "spriteSourceSize": { "x": 0, "y": 0, "w": 16, "h": 16 }, "sourceSize": { "w": 16, "h": 16 }, "duration": 100 },
                                        "hero 1.aseprite": { "frame": { "x": 16, "y": 0, "w": 16, "h": 16 }, "rotated": false, "trimmed": false, "spriteSourceSize": { "x": 0, "y": 0, "w": 16, "h": 16 }, "sourceSize": { "w": 16, "h": 16 }, "duration": 250 },
                                        "hero 2.aseprite": { "frame": { "x": 32, "y": 0, "w": 16, "h": 16 }, "rotated": false, "trimmed": false, "spriteSourceSize": { "x": 0, "y": 0, "w": 16, "h": 16 }, "sourceSize": { "w": 16, "h": 16 }, "duration": 100 },
                                        "hero 3.aseprite": { "frame": { "x": 48, "y": 0, "w": 16, "h": 16 }, "rotated": false, "trimmed": false, "spriteSourceSize": { "x": 0, "y": 0, "w": 16, "h": 16 }, "sourceSize": { "w": 16, "h": 16 }, "duration": 100 }
                                      },
                                      "meta": {
                                        "app": "https://www.aseprite.org/",
                                        "version": "1.3.2",
                                        "image": "hero.png",
                                        "format": "RGBA8888",
                                        "size": { "w": 64, "h": 16 },
                                        "scale": "1",
                                        "frameTags": [
                                          { "name": "walk", "from": 0, "to": 1, "direction": "forward", "color": "#000000ff" },
                                          { "name": "back", "from": 1, "to": 3, "direction": "reverse", "color": "#000000ff" },
                                          { "name": "bounce", "from": 0, "to": 3, "direction": "pingpong", "color": "#000000ff" }
                                        ]
                                      }
                                    }
                                    """;

    private const string ArrayJson = """
                                     {
                                       "frames": [
                                         { "filename": "blob 0.aseprite", "frame": { "x": 0, "y": 0, "w": 8, "h": 8 }, "rotated": false, "trimmed": false, "sourceSize": { "w": 8, "h": 8 }, "duration": 50 },
                                         { "filename": "blob 1.aseprite", "frame": { "x": 8, "y": 0, "w": 8, "h": 8 }, "rotated": false, "trimmed": false, "sourceSize": { "w": 8, "h": 8 }, "duration": 75 },
                                         { "filename": "blob 2.aseprite", "frame": { "x": 0, "y": 8, "w": 8, "h": 8 }, "rotated": false, "trimmed": false, "sourceSize": { "w": 8, "h": 8 }, "duration": 125 }
                                       ],
                                       "meta": { "image": "blob.png", "size": { "w": 16, "h": 16 }, "scale": "1" }
                                     }
                                     """;

    // ── Parse ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_HashForm_ReadsFramesInOrder_WithDurationsInSeconds()
    {
        var doc = AsepriteImport.Parse(HashJson);

        Assert.Equal(expected: 4, actual: doc.Frames.Count);
        Assert.Equal(expected: "hero 0.aseprite", actual: doc.Frames[0].Name);
        Assert.Equal(
            expected: (16, 0, 16, 16),
            actual: (doc.Frames[1].X, doc.Frames[1].Y, doc.Frames[1].W, doc.Frames[1].H)
        );
        Assert.Equal(expected: 0.1f, actual: doc.Frames[0].DurationSeconds, precision: 5);
        Assert.Equal(expected: 0.25f, actual: doc.Frames[1].DurationSeconds, precision: 5);
        Assert.Equal(expected: 64, actual: doc.SheetWidth);
        Assert.Equal(expected: 16, actual: doc.SheetHeight);
        Assert.Equal(expected: 3, actual: doc.Tags.Count);
    }

    [Fact]
    public void Parse_ArrayForm_ReadsFilenamesRectsAndDurations()
    {
        var doc = AsepriteImport.Parse(ArrayJson);

        Assert.Equal(expected: 3, actual: doc.Frames.Count);
        Assert.Equal(expected: "blob 1.aseprite", actual: doc.Frames[1].Name);
        Assert.Equal(
            expected: (0, 8, 8, 8),
            actual: (doc.Frames[2].X, doc.Frames[2].Y, doc.Frames[2].W, doc.Frames[2].H)
        );
        Assert.Equal(expected: 0.125f, actual: doc.Frames[2].DurationSeconds, precision: 5);
        Assert.Empty(doc.Tags);
    }

    [Fact]
    public void Parse_ReadsTagRangesAndDirections()
    {
        var doc = AsepriteImport.Parse(HashJson);

        Assert.Equal(
            expected: new AsepriteTag(
                Name: "walk",
                From: 0,
                To: 1,
                Direction: "forward"
            ),
            actual: doc.Tags[0]
        );
        Assert.Equal(
            expected: new AsepriteTag(
                Name: "back",
                From: 1,
                To: 3,
                Direction: "reverse"
            ),
            actual: doc.Tags[1]
        );
        Assert.Equal(
            expected: new AsepriteTag(
                Name: "bounce",
                From: 0,
                To: 3,
                Direction: "pingpong"
            ),
            actual: doc.Tags[2]
        );
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => AsepriteImport.Parse("{ not json"));
        Assert.Throws<FormatException>(() => AsepriteImport.Parse("{}"));
        Assert.Throws<FormatException>(() => AsepriteImport.Parse("""{ "frames": 3 }"""));
        Assert.Throws<FormatException>(() =>
            AsepriteImport.Parse("""{ "frames": { "a": { "duration": 100 } } }""")
        );
        Assert.Throws<FormatException>(() =>
            AsepriteImport.Parse(
                """{ "frames": { "a": { "frame": { "x": 0, "y": 0, "w": 8 } } } }"""
            )
        );
    }

    // ── ToClips ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToClips_ForwardTag_LoopsInSourceOrder()
    {
        var clips = AsepriteImport.ToClips(
            document: AsepriteImport.Parse(HashJson),
            textureWidth: 64,
            textureHeight: 16
        );

        var walk = clips[0];
        Assert.Equal(expected: "walk", actual: walk.Name);
        Assert.Equal(expected: SpriteLoopMode.Loop, actual: walk.Loop);
        Assert.Equal(expected: 2, actual: walk.FrameCount);
        Assert.Equal(expected: 0f, actual: walk.Frames[0].U0, precision: 5);
        Assert.Equal(expected: 0.25f, actual: walk.Frames[1].U0, precision: 5);
        Assert.Equal(expected: 0.1f, actual: walk.DurationAt(0), precision: 5);
        Assert.Equal(expected: 0.25f, actual: walk.DurationAt(1), precision: 5);
    }

    [Fact]
    public void ToClips_ReverseTag_ReversesFramesAndDurations()
    {
        var clips = AsepriteImport.ToClips(
            document: AsepriteImport.Parse(HashJson),
            textureWidth: 64,
            textureHeight: 16
        );

        var back = clips[1];
        Assert.Equal(expected: "back", actual: back.Name);
        Assert.Equal(expected: SpriteLoopMode.Loop, actual: back.Loop);
        Assert.Equal(expected: 3, actual: back.FrameCount);
        Assert.Equal(
            expected: 0.75f,
            actual: back.Frames[0].U0,
            precision: 5
        ); // source frame 3 first
        Assert.Equal(expected: 0.5f, actual: back.Frames[1].U0, precision: 5);
        Assert.Equal(expected: 0.25f, actual: back.Frames[2].U0, precision: 5);
        Assert.Equal(
            expected: 0.1f,
            actual: back.DurationAt(0),
            precision: 5
        ); // durations travel with their frames
        Assert.Equal(expected: 0.25f, actual: back.DurationAt(2), precision: 5);
    }

    [Fact]
    public void ToClips_PingPongTag_MapsToPingPongLoopMode()
    {
        var clips = AsepriteImport.ToClips(
            document: AsepriteImport.Parse(HashJson),
            textureWidth: 64,
            textureHeight: 16
        );

        var bounce = clips[2];
        Assert.Equal(expected: "bounce", actual: bounce.Name);
        Assert.Equal(expected: SpriteLoopMode.PingPong, actual: bounce.Loop);
        Assert.Equal(expected: 4, actual: bounce.FrameCount);
        Assert.Equal(
            expected: 0f,
            actual: bounce.Frames[0].U0,
            precision: 5
        ); // pingpong keeps forward source order
        Assert.Equal(expected: 0.75f, actual: bounce.Frames[3].U0, precision: 5);
    }

    [Fact]
    public void ToClips_NoTags_YieldsOneDefaultClipOverAllFrames()
    {
        var clips = AsepriteImport.ToClips(
            document: AsepriteImport.Parse(ArrayJson),
            textureWidth: 16,
            textureHeight: 16
        );

        var clip = Assert.Single(clips);
        Assert.Equal(expected: "default", actual: clip.Name);
        Assert.Equal(expected: SpriteLoopMode.Loop, actual: clip.Loop);
        Assert.Equal(expected: 3, actual: clip.FrameCount);
        Assert.Equal(expected: 0.25f, actual: clip.Duration, precision: 5); // 0.05 + 0.075 + 0.125
    }

    [Fact]
    public void ToClips_PixelRects_MatchGridFramesUvConversion()
    {
        // The same 4×2 grid of 16×16 cells on a 64×32 sheet, expressed as Aseprite frames.
        var sb = new StringBuilder("{ \"frames\": {");
        for (int i = 0; i < 8; i++)
        {
            int x = i % 4 * 16;
            int y = i / 4 * 16;
            if (i > 0) sb.Append(',');
            sb.Append(
                $"\"g {i}\": {{ \"frame\": {{ \"x\": {x}, \"y\": {y}, \"w\": 16, \"h\": 16 }}, \"duration\": 100 }}"
            );
        }

        sb.Append("}, \"meta\": { \"size\": { \"w\": 64, \"h\": 32 } } }");

        var clip = Assert.Single(
            AsepriteImport.ToClips(
                document: AsepriteImport.Parse(sb.ToString()),
                textureWidth: 64,
                textureHeight: 32
            )
        );
        var expected = SpriteSheet.GridFrames(
            texWidth: 64,
            texHeight: 32,
            cols: 4,
            rows: 2
        );

        Assert.Equal(expected: expected.Length, actual: clip.FrameCount);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected: expected[i], actual: clip.Frames[i]);
    }

    [Fact]
    public void ToClips_TextureOverload_UsesTheTextureDimensions()
    {
        var texture = SpriteTexture.FromPixels(
            device: new NullSpriteDevice(),
            rgba: new byte[64 * 16 * 4],
            width: 64,
            height: 16
        )!;
        var doc = AsepriteImport.Parse(HashJson);

        var fromTexture = AsepriteImport.ToClips(document: doc, texture: texture);
        var fromSize = AsepriteImport.ToClips(document: doc, textureWidth: 64, textureHeight: 16);

        Assert.Equal(expected: fromSize.Count, actual: fromTexture.Count);
        for (int c = 0; c < fromSize.Count; c++)
        for (int i = 0; i < fromSize[c].FrameCount; i++)
            Assert.Equal(expected: fromSize[c].Frames[i], actual: fromTexture[c].Frames[i]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class NullSpriteDevice : ISpriteDevice
    {
        public uint CreateTexture(ReadOnlySpan<byte> rgba, int width, int height,
            SpriteFilter filter, bool srgb,
            SpriteWrap wrap) =>
            1;

        public uint CreateTextureFromFile(string path, SpriteFilter filter, bool srgb,
            SpriteWrap wrap,
            out int width, out int height)
        {
            width = 0;
            height = 0;
            return 0;
        }

        public void DestroyTexture(uint texture) { }

        public uint CreateShader(string wgsl) => 1;

        public void Begin(ReadOnlySpan<float> sceneViewProj, ReadOnlySpan<float> overlayViewProj,
            float viewportW,
            float viewportH) { }

        public void Submit(uint texture, uint texture2, uint shader, Blend2D blend, Stage2D stage,
            ReadOnlySpan<float> materialParams, ReadOnlySpan<float> instances, int count) { }
    }
}
