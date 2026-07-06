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

        Assert.Equal(4, doc.Frames.Count);
        Assert.Equal("hero 0.aseprite", doc.Frames[0].Name);
        Assert.Equal(
            (16, 0, 16, 16),
            (doc.Frames[1].X, doc.Frames[1].Y, doc.Frames[1].W, doc.Frames[1].H)
        );
        Assert.Equal(0.1f, doc.Frames[0].DurationSeconds, 5);
        Assert.Equal(0.25f, doc.Frames[1].DurationSeconds, 5);
        Assert.Equal(64, doc.SheetWidth);
        Assert.Equal(16, doc.SheetHeight);
        Assert.Equal(3, doc.Tags.Count);
    }

    [Fact]
    public void Parse_ArrayForm_ReadsFilenamesRectsAndDurations()
    {
        var doc = AsepriteImport.Parse(ArrayJson);

        Assert.Equal(3, doc.Frames.Count);
        Assert.Equal("blob 1.aseprite", doc.Frames[1].Name);
        Assert.Equal(
            (0, 8, 8, 8),
            (doc.Frames[2].X, doc.Frames[2].Y, doc.Frames[2].W, doc.Frames[2].H)
        );
        Assert.Equal(0.125f, doc.Frames[2].DurationSeconds, 5);
        Assert.Empty(doc.Tags);
    }

    [Fact]
    public void Parse_ReadsTagRangesAndDirections()
    {
        var doc = AsepriteImport.Parse(HashJson);

        Assert.Equal(
            new AsepriteTag(
                "walk",
                0,
                1,
                "forward"
            ),
            doc.Tags[0]
        );
        Assert.Equal(
            new AsepriteTag(
                "back",
                1,
                3,
                "reverse"
            ),
            doc.Tags[1]
        );
        Assert.Equal(
            new AsepriteTag(
                "bounce",
                0,
                3,
                "pingpong"
            ),
            doc.Tags[2]
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
        var clips = AsepriteImport.ToClips(AsepriteImport.Parse(HashJson), 64, 16);

        var walk = clips[0];
        Assert.Equal("walk", walk.Name);
        Assert.Equal(SpriteLoopMode.Loop, walk.Loop);
        Assert.Equal(2, walk.FrameCount);
        Assert.Equal(0f, walk.Frames[0].U0, 5);
        Assert.Equal(0.25f, walk.Frames[1].U0, 5);
        Assert.Equal(0.1f, walk.DurationAt(0), 5);
        Assert.Equal(0.25f, walk.DurationAt(1), 5);
    }

    [Fact]
    public void ToClips_ReverseTag_ReversesFramesAndDurations()
    {
        var clips = AsepriteImport.ToClips(AsepriteImport.Parse(HashJson), 64, 16);

        var back = clips[1];
        Assert.Equal("back", back.Name);
        Assert.Equal(SpriteLoopMode.Loop, back.Loop);
        Assert.Equal(3, back.FrameCount);
        Assert.Equal(0.75f, back.Frames[0].U0, 5); // source frame 3 first
        Assert.Equal(0.5f, back.Frames[1].U0, 5);
        Assert.Equal(0.25f, back.Frames[2].U0, 5);
        Assert.Equal(0.1f, back.DurationAt(0), 5); // durations travel with their frames
        Assert.Equal(0.25f, back.DurationAt(2), 5);
    }

    [Fact]
    public void ToClips_PingPongTag_MapsToPingPongLoopMode()
    {
        var clips = AsepriteImport.ToClips(AsepriteImport.Parse(HashJson), 64, 16);

        var bounce = clips[2];
        Assert.Equal("bounce", bounce.Name);
        Assert.Equal(SpriteLoopMode.PingPong, bounce.Loop);
        Assert.Equal(4, bounce.FrameCount);
        Assert.Equal(0f, bounce.Frames[0].U0, 5); // pingpong keeps forward source order
        Assert.Equal(0.75f, bounce.Frames[3].U0, 5);
    }

    [Fact]
    public void ToClips_NoTags_YieldsOneDefaultClipOverAllFrames()
    {
        var clips = AsepriteImport.ToClips(AsepriteImport.Parse(ArrayJson), 16, 16);

        var clip = Assert.Single(clips);
        Assert.Equal("default", clip.Name);
        Assert.Equal(SpriteLoopMode.Loop, clip.Loop);
        Assert.Equal(3, clip.FrameCount);
        Assert.Equal(0.25f, clip.Duration, 5); // 0.05 + 0.075 + 0.125
    }

    [Fact]
    public void ToClips_PixelRects_MatchGridFramesUvConversion()
    {
        // The same 4×2 grid of 16×16 cells on a 64×32 sheet, expressed as Aseprite frames.
        var sb = new StringBuilder("{ \"frames\": {");
        for (var i = 0; i < 8; i++)
        {
            var x = i % 4 * 16;
            var y = i / 4 * 16;
            if (i > 0) sb.Append(',');
            sb.Append(
                $"\"g {i}\": {{ \"frame\": {{ \"x\": {x}, \"y\": {y}, \"w\": 16, \"h\": 16 }}, \"duration\": 100 }}"
            );
        }

        sb.Append("}, \"meta\": { \"size\": { \"w\": 64, \"h\": 32 } } }");

        var clip = Assert.Single(
            AsepriteImport.ToClips(AsepriteImport.Parse(sb.ToString()), 64, 32)
        );
        var expected = SpriteSheet.GridFrames(
            64,
            32,
            4,
            2
        );

        Assert.Equal(expected.Length, clip.FrameCount);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], clip.Frames[i]);
    }

    [Fact]
    public void ToClips_TextureOverload_UsesTheTextureDimensions()
    {
        var texture = SpriteTexture.FromPixels(
            new NullSpriteDevice(),
            new byte[64 * 16 * 4],
            64,
            16
        )!;
        var doc = AsepriteImport.Parse(HashJson);

        var fromTexture = AsepriteImport.ToClips(doc, texture);
        var fromSize = AsepriteImport.ToClips(doc, 64, 16);

        Assert.Equal(fromSize.Count, fromTexture.Count);
        for (var c = 0; c < fromSize.Count; c++)
        for (var i = 0; i < fromSize[c].FrameCount; i++)
            Assert.Equal(fromSize[c].Frames[i], fromTexture[c].Frames[i]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class NullSpriteDevice : ISpriteDevice
    {
        public uint CreateTexture(ReadOnlySpan<byte> rgba, int width, int height,
            SpriteFilter filter, bool srgb,
            SpriteWrap wrap)
        {
            return 1;
        }

        public uint CreateTextureFromFile(string path, SpriteFilter filter, bool srgb,
            SpriteWrap wrap,
            out int width, out int height)
        {
            width = 0;
            height = 0;
            return 0;
        }

        public void DestroyTexture(uint texture)
        {
        }

        public uint CreateShader(string wgsl)
        {
            return 1;
        }

        public void Begin(ReadOnlySpan<float> sceneViewProj, ReadOnlySpan<float> overlayViewProj,
            float viewportW,
            float viewportH)
        {
        }

        public void Submit(uint texture, uint texture2, uint shader, Blend2D blend, Stage2D stage,
            ReadOnlySpan<float> materialParams, ReadOnlySpan<float> instances, int count)
        {
        }
    }
}