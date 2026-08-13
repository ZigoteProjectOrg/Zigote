using Xunit;
using Zigote.Core.Math3D;
using Zigote.Render2D;

namespace Zigote.Tests;

public class Render2DTests
{
    /// <summary>Floats per packed instance — kept in step with the renderer/shader layout.</summary>
    private const int Stride = Core.Engine.ZigoteEngine.SpriteInstanceFloats;

    // ── SpriteSheet.GridFrames ───────────────────────────────────────────────

    [Fact]
    public void GridFrames_PlainGrid_ComputesUvsAndPixelSizes()
    {
        var frames = SpriteSheet.GridFrames(
            64,
            32,
            4,
            2
        );

        Assert.Equal(8, frames.Length);
        Assert.Equal(
            new SpriteFrame(
                0f,
                0f,
                0.25f,
                0.5f,
                16,
                16
            ),
            frames[0]
        );
        Assert.Equal(
            new SpriteFrame(
                0.25f,
                0f,
                0.5f,
                0.5f,
                16,
                16
            ),
            frames[1]
        );
        Assert.Equal(
            new SpriteFrame(
                0.75f,
                0.5f,
                1f,
                1f,
                16,
                16
            ),
            frames[7]
        );
    }

    [Fact]
    public void GridFrames_IsRowMajor()
    {
        var frames = SpriteSheet.GridFrames(
            64,
            32,
            4,
            2
        );

        // frames[cols] is the first frame of row 1: same U as frame 0, V shifted a row down.
        Assert.Equal(frames[0].U0, frames[4].U0, 5);
        Assert.Equal(0.5f, frames[4].V0, 5);
        Assert.Equal(1f, frames[4].V1, 5);
    }

    [Fact]
    public void GridFrames_MarginAndSpacing_ShiftCells()
    {
        var frames = SpriteSheet.GridFrames(
            70,
            38,
            2,
            2,
            3,
            3,
            4,
            4
        );

        // cellW = (70 - 2*3 - 1*4) / 2 = 30, cellH = (38 - 2*3 - 1*4) / 2 = 14.
        Assert.Equal(4, frames.Length);
        Assert.Equal(30, frames[0].PixelWidth);
        Assert.Equal(14, frames[0].PixelHeight);
        Assert.Equal(3f / 70f, frames[0].U0, 5);
        Assert.Equal(3f / 38f, frames[0].V0, 5);
        Assert.Equal(33f / 70f, frames[0].U1, 5);
        Assert.Equal(17f / 38f, frames[0].V1, 5);
        Assert.Equal(37f / 70f, frames[1].U0, 5); // x = 3 + (30 + 4)
        Assert.Equal(21f / 38f, frames[2].V0, 5); // y = 3 + (14 + 4)
        Assert.Equal(35f / 38f, frames[2].V1, 5);
    }

    [Fact]
    public void SpriteSheet_Frame_ClampsIndex()
    {
        var device = new FakeSpriteDevice();
        var texture = SpriteTexture.FromPixels(
            device,
            new byte[64 * 32 * 4],
            64,
            32
        )!;
        var sheet = SpriteSheet.FromGrid(texture, 4, 2);

        Assert.Equal(8, sheet.FrameCount);
        Assert.Equal(sheet.Frames[0], sheet.Frame(-5));
        Assert.Equal(sheet.Frames[7], sheet.Frame(99));
    }

    // ── DynamicTextureAtlas ──────────────────────────────────────────────────

    [Fact]
    public void Atlas_EntriesStayInBounds_AndKeepPaddingApart()
    {
        const int padding = 2;
        using var atlas = new DynamicTextureAtlas(new NullSpriteDevice(), 128, 256);

        int[][] sizes =
            [[40, 30], [50, 20], [60, 25], [30, 40], [20, 20], [45, 15], [25, 25], [35, 35]];
        var sprites = new List<AtlasSprite>();
        foreach (var s in sizes)
        {
            var sprite = atlas.TryAdd(new byte[s[0] * s[1] * 4], s[0], s[1]);
            Assert.NotNull(sprite);
            sprites.Add(sprite);
        }

        foreach (var s in sprites)
        {
            Assert.True(s.PixelX >= 0 && s.PixelY >= 0);
            Assert.True(s.PixelX + s.PixelWidth <= atlas.Size);
            Assert.True(s.PixelY + s.PixelHeight <= atlas.Size);
        }

        for (var i = 0; i < sprites.Count; i++)
        for (var j = i + 1; j < sprites.Count; j++)
            Assert.False(
                CloserThanPadding(sprites[i], sprites[j], padding),
                $"entries {i} and {j} overlap or sit closer than {padding}px"
            );
    }

    [Fact]
    public void Atlas_Growth_RepacksAndRenormalizesExistingFrames()
    {
        using var atlas = new DynamicTextureAtlas(new NullSpriteDevice(), 64, 256);

        var a = atlas.TryAdd(new byte[32 * 32 * 4], 32, 32)!;
        Assert.Equal(64, atlas.Size);
        Assert.Equal(
            new SpriteFrame(
                0f,
                0f,
                0.5f,
                0.5f,
                32,
                32
            ),
            a.Frame
        );

        // A second 32×32 can't fit at 64 (shelf 2 would start at y=34): grows to 128 and repacks.
        var b = atlas.TryAdd(new byte[32 * 32 * 4], 32, 32)!;
        Assert.Equal(128, atlas.Size);

        // The existing sprite's frame was renormalized against the NEW size...
        Assert.Equal(
            new SpriteFrame(
                0f,
                0f,
                0.25f,
                0.25f,
                32,
                32
            ),
            a.Frame
        );
        // ...and the new one sits a padding gap to its right, on the same shelf.
        Assert.Equal(34, b.PixelX);
        Assert.Equal(0, b.PixelY);
        Assert.Equal(
            new SpriteFrame(
                34f / 128f,
                0f,
                66f / 128f,
                0.25f,
                32,
                32
            ),
            b.Frame
        );
    }

    [Fact]
    public void Atlas_FramesAlwaysMatchPixelRects()
    {
        using var atlas = new DynamicTextureAtlas(new NullSpriteDevice(), 64, 512);

        var sprites = new List<AtlasSprite>();
        for (var i = 0; i < 12; i++)
            sprites.Add(atlas.TryAdd(new byte[48 * 24 * 4], 48, 24)!);

        var inv = 1f / atlas.Size;
        foreach (var s in sprites)
        {
            Assert.Equal(s.PixelX * inv, s.Frame.U0, 5);
            Assert.Equal(s.PixelY * inv, s.Frame.V0, 5);
            Assert.Equal((s.PixelX + s.PixelWidth) * inv, s.Frame.U1, 5);
            Assert.Equal((s.PixelY + s.PixelHeight) * inv, s.Frame.V1, 5);
        }
    }

    [Fact]
    public void Atlas_TryAdd_TooBigReturnsNull_AndPreservesExistingRects()
    {
        using var atlas = new DynamicTextureAtlas(new NullSpriteDevice(), 64, 64);

        var a = atlas.TryAdd(new byte[40 * 40 * 4], 40, 40)!;
        var frameBefore = a.Frame;
        var xBefore = a.PixelX;
        var yBefore = a.PixelY;

        // Bigger than maxSize in one dimension: rejected outright.
        Assert.Null(atlas.TryAdd(new byte[100 * 10 * 4], 100, 10));
        // Fits dimensionally but not capacity-wise, and the atlas is already at maxSize.
        Assert.Null(atlas.TryAdd(new byte[40 * 40 * 4], 40, 40));

        Assert.Equal(frameBefore, a.Frame);
        Assert.Equal(xBefore, a.PixelX);
        Assert.Equal(yBefore, a.PixelY);
        Assert.Equal(64, atlas.Size);
    }

    [Fact]
    public void Atlas_Commit_UploadsPixels_ReplacesTexture_AndIsNoOpWhenClean()
    {
        var device = new FakeSpriteDevice();
        using var atlas = new DynamicTextureAtlas(device, 64, 64);
        Assert.Equal(0u, atlas.TextureHandle);

        byte[] rgba = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160];
        var sprite = atlas.TryAdd(rgba, 2, 2)!;
        atlas.Commit();

        var first = atlas.TextureHandle;
        Assert.NotEqual(0u, first);
        Assert.Equal(1, device.CreateTextureCalls);
        Assert.Equal(64, device.LastTextureWidth);

        // The entry's pixels landed at its rect (row 1 starts one atlas stride down).
        var stride = atlas.Size * 4;
        var o = sprite.PixelY * stride + sprite.PixelX * 4;
        Assert.Equal(rgba[..8], device.LastTexturePixels[o..(o + 8)]);
        Assert.Equal(rgba[8..], device.LastTexturePixels[(o + stride)..(o + stride + 8)]);

        atlas.Commit(); // clean → no-op
        Assert.Equal(1, device.CreateTextureCalls);

        atlas.TryAdd(new byte[3 * 3 * 4], 3, 3);
        atlas.Commit(); // dirty → destroys the old texture, creates a new one
        Assert.Equal(2, device.CreateTextureCalls);
        Assert.Contains(first, device.Destroyed);
        Assert.NotEqual(first, atlas.TextureHandle);
    }

    // ── Renderer2D: sorting ──────────────────────────────────────────────────

    [Fact]
    public void Renderer_EqualKeys_PreserveSubmissionOrder()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);

        renderer.Begin(
            Mat4.Identity,
            Mat4.Identity,
            800f,
            600f
        );
        for (var i = 0; i < 4; i++)
            renderer.Draw(MakeDraw(7, i));
        renderer.End();

        var batch = Assert.Single(device.Submits);
        Assert.Equal(4, batch.Count);
        for (var i = 0; i < 4; i++)
            Assert.Equal(i, batch.Instances[i * Stride + 10], 5);
    }

    [Fact]
    public void Renderer_SortsLayersBeforeOrders()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);

        renderer.Begin(
            Mat4.Identity,
            Mat4.Identity,
            800f,
            600f
        );
        renderer.Draw(
            MakeDraw(
                7,
                0,
                1,
                -100
            )
        );
        renderer.Draw(
            MakeDraw(
                7,
                1,
                0,
                100
            )
        );
        renderer.Draw(
            MakeDraw(
                7,
                2,
                0,
                -5
            )
        );
        renderer.Draw(
            MakeDraw(
                7,
                3,
                -2,
                32767
            )
        );
        renderer.End();

        var batch = Assert.Single(device.Submits);
        float[] expectedOrder = [3f, 2f, 1f, 0f]; // layer -2, then (0,-5), (0,100), then layer 1
        for (var i = 0; i < 4; i++)
            Assert.Equal(expectedOrder[i], batch.Instances[i * Stride + 10], 5);
    }

    // ── Renderer2D: batching ─────────────────────────────────────────────────

    [Fact]
    public void Renderer_SplitsBatches_OnTextureBlendStageAndMaterialReference()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);
        var additive = new Material2D { Blend = Blend2D.Additive };
        var overlay = new Material2D { Stage = Stage2D.Overlay };
        var cloneOfDefault = new Material2D(); // identical values, different reference

        renderer.Begin(
            Mat4.Identity,
            Mat4.Identity,
            800f,
            600f
        );
        renderer.Draw(MakeDraw(1));
        renderer.Draw(MakeDraw(1)); // same key → no split
        renderer.Draw(MakeDraw(2)); // texture change
        renderer.Draw(MakeDraw(2, material: additive)); // blend change
        renderer.Draw(MakeDraw(2, material: overlay)); // stage change
        renderer.Draw(MakeDraw(2, material: cloneOfDefault)); // reference change only
        renderer.End();

        Assert.Equal(6, renderer.DrawCount);
        Assert.Equal(5, renderer.BatchCount);
        Assert.Equal(5, device.Submits.Count);

        Assert.Equal((1u, 2, Blend2D.Alpha, Stage2D.Scene), Key(device.Submits[0]));
        Assert.Equal((2u, 1, Blend2D.Alpha, Stage2D.Scene), Key(device.Submits[1]));
        Assert.Equal((2u, 1, Blend2D.Additive, Stage2D.Scene), Key(device.Submits[2]));
        Assert.Equal((2u, 1, Blend2D.Alpha, Stage2D.Overlay), Key(device.Submits[3]));
        Assert.Equal((2u, 1, Blend2D.Alpha, Stage2D.Scene), Key(device.Submits[4]));

        static (uint, int, Blend2D, Stage2D) Key(FakeSpriteDevice.SubmitCall c)
        {
            return (c.Texture, c.Count, c.Blend, c.Stage);
        }
    }

    [Fact]
    public void Renderer_DoesNotValidateHandles_ZeroTextureStillSubmits()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);

        renderer.Begin(
            Mat4.Identity,
            Mat4.Identity,
            800f,
            600f
        );
        renderer.Draw(MakeDraw(0));
        renderer.End();

        var batch = Assert.Single(device.Submits);
        Assert.Equal(0u, batch.Texture);
        Assert.Equal(1, batch.Count);
    }

    [Fact]
    public void Renderer_Begin_ForwardsColumnMajorMatricesAndViewport()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);
        var scene = Mat4.Translation(new Vec3(1f, 2f, 3f));
        var overlayVp = Camera2D.PixelOverlay(320f, 240f);

        renderer.Begin(
            scene,
            overlayVp,
            320f,
            240f
        );

        Assert.Equal(1, device.BeginCalls);
        Assert.Equal(scene.ToArray(), device.LastSceneVp);
        Assert.Equal(overlayVp.ToArray(), device.LastOverlayVp);
        Assert.Equal(320f, device.LastViewportW);
        Assert.Equal(240f, device.LastViewportH);
    }

    // ── Renderer2D: instance packing ─────────────────────────────────────────

    [Fact]
    public void Renderer_PacksPivotRotationIntoPosition()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);

        // 90° CCW with a bottom-left pivot: center offset (2, 1) rotates to (-1, 2).
        var draw = new SpriteDraw {
            X = 10f,
            Y = 20f,
            Z = 3f,
            Rotation = MathF.PI / 2f,
            Width = 4f,
            Height = 2f,
            PivotX = 0f,
            PivotY = 0f,
            Frame = new SpriteFrame(
                0.1f,
                0.2f,
                0.3f,
                0.4f,
                4,
                2
            ),
            Color = new Vec4(
                0.25f,
                0.5f,
                0.75f,
                1f
            ),
            Texture = 1,
        };

        renderer.Begin(
            Mat4.Identity,
            Mat4.Identity,
            800f,
            600f
        );
        renderer.Draw(in draw);
        renderer.End();

        var inst = Assert.Single(device.Submits).Instances;
        Assert.Equal(9f, inst[0], 4);
        Assert.Equal(22f, inst[1], 4);
        Assert.Equal(3f, inst[2], 5);
        Assert.Equal(MathF.PI / 2f, inst[3], 5);
        Assert.Equal(4f, inst[4], 5);
        Assert.Equal(2f, inst[5], 5);
        Assert.Equal(0.1f, inst[6], 5);
        Assert.Equal(0.2f, inst[7], 5);
        Assert.Equal(0.3f, inst[8], 5);
        Assert.Equal(0.4f, inst[9], 5);
        Assert.Equal(0.25f, inst[10], 5);
        Assert.Equal(0.5f, inst[11], 5);
        Assert.Equal(0.75f, inst[12], 5);
        Assert.Equal(1f, inst[13], 5);
    }

    [Fact]
    public void Renderer_CenterPivot_LeavesPositionUnchanged_AndFlipsSwapUvs()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);
        var draw = new SpriteDraw {
            X = 5f,
            Y = -7f,
            Rotation = 1.3f, // rotation must not move a center pivot
            Width = 6f,
            Height = 3f,
            PivotX = 0.5f,
            PivotY = 0.5f,
            Frame = new SpriteFrame(
                0.1f,
                0.2f,
                0.3f,
                0.4f,
                6,
                3
            ),
            Color = Vec4.One,
            FlipX = true,
            FlipY = true,
            Texture = 1,
        };

        renderer.Begin(
            Mat4.Identity,
            Mat4.Identity,
            800f,
            600f
        );
        renderer.Draw(in draw);
        renderer.End();

        var inst = Assert.Single(device.Submits).Instances;
        Assert.Equal(5f, inst[0], 5);
        Assert.Equal(-7f, inst[1], 5);
        Assert.Equal(0.3f, inst[6], 5); // u0 ↔ u1
        Assert.Equal(0.4f, inst[7], 5); // v0 ↔ v1
        Assert.Equal(0.1f, inst[8], 5);
        Assert.Equal(0.2f, inst[9], 5);
    }

    // ── Renderer2D: zero allocation ──────────────────────────────────────────

    [Fact]
    public void Renderer_SecondIdenticalFrame_AllocatesZero()
    {
        var renderer = new Renderer2D(new NullSpriteDevice());
        var shared = new Material2D { Blend = Blend2D.Additive };
        var scene = Mat4.Identity;
        var overlayVp = Camera2D.PixelOverlay(800f, 600f);

        // Warm up past tiered JIT and grow every internal buffer to its steady-state size.
        for (var i = 0; i < 200; i++)
            Frame(
                renderer,
                scene,
                overlayVp,
                shared
            );
        Assert.Equal(32, renderer.DrawCount);
        Assert.True(renderer.BatchCount > 1);

        const int frames = 500;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++)
            Frame(
                renderer,
                scene,
                overlayVp,
                shared
            );
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"Renderer2D frame allocated {allocated} B over {frames} frames " +
            $"({allocated / (double)frames:F2} B/frame); expected 0."
        );

        static void Frame(Renderer2D renderer, in Mat4 scene, in Mat4 overlayVp, Material2D shared)
        {
            renderer.Begin(
                scene,
                overlayVp,
                800f,
                600f
            );
            for (var i = 0; i < 32; i++)
            {
                var draw = new SpriteDraw {
                    X = i,
                    Y = -i,
                    Z = 0.5f,
                    Rotation = i * 0.1f,
                    Width = 2f,
                    Height = 1f,
                    PivotX = 0.25f,
                    PivotY = 0.75f,
                    Frame = new SpriteFrame(
                        0f,
                        0f,
                        1f,
                        1f,
                        32,
                        32
                    ),
                    Color = Vec4.One,
                    FlipX = (i & 1) != 0,
                    SortingLayer = (short)(i % 3),
                    OrderInLayer = (short)(i % 5),
                    Texture = (uint)(1 + i % 2),
                    Material = (i & 1) != 0 ? shared : null,
                };
                renderer.Draw(in draw);
            }

            renderer.End();
        }
    }

    // ── Camera2D ─────────────────────────────────────────────────────────────

    [Fact]
    public void Camera_ViewProjection_MapsPositionToNdcOrigin()
    {
        var camera = new Camera2D {
            Position = new Vec2(3f, 4f),
            Rotation = 0.7f,
            OrthoHeight = 12f,
        };
        var vp = camera.ViewProjection(800f, 600f);

        var ndc = vp.MulPoint(new Vec3(3f, 4f, 0f));
        Assert.Equal(0f, ndc.X, 4);
        Assert.Equal(0f, ndc.Y, 4);
        Assert.InRange(ndc.Z, 0f, 1f); // wgpu 0..1 depth
    }

    [Fact]
    public void Camera_ViewProjection_IsYUp_AndScalesByOrthoHeightAndZoom()
    {
        var camera = new Camera2D { OrthoHeight = 10f };
        var vp = camera.ViewProjection(800f, 600f);

        // Half the visible height above center lands on NDC +1 (Y up).
        Assert.Equal(1f, vp.MulPoint(new Vec3(0f, 5f, 0f)).Y, 4);
        // Half the visible width (halfH · aspect) lands on NDC +1.
        Assert.Equal(1f, vp.MulPoint(new Vec3(5f * (800f / 600f), 0f, 0f)).X, 4);

        camera.Zoom = 2f; // halves the visible height
        vp = camera.ViewProjection(800f, 600f);
        Assert.Equal(1f, vp.MulPoint(new Vec3(0f, 2.5f, 0f)).Y, 4);
    }

    [Fact]
    public void Camera_Rotation_TurnsTheViewCcw()
    {
        var camera = new Camera2D {
            Rotation = MathF.PI / 2f,
            OrthoHeight = 10f,
        };
        var vp = camera.ViewProjection(600f, 600f);

        // Camera rotated 90° CCW: its up axis points at world -X, so (-5, 0) is the top edge.
        var ndc = vp.MulPoint(new Vec3(-5f, 0f, 0f));
        Assert.Equal(0f, ndc.X, 4);
        Assert.Equal(1f, ndc.Y, 4);
    }

    [Fact]
    public void Camera_PixelOverlay_MapsTopLeftAndBottomRightCorners()
    {
        var vp = Camera2D.PixelOverlay(800f, 600f);

        var topLeft = vp.MulPoint(new Vec3(0f, 0f, 0f));
        Assert.Equal(-1f, topLeft.X, 4);
        Assert.Equal(1f, topLeft.Y, 4);

        var bottomRight = vp.MulPoint(new Vec3(800f, 600f, 0f));
        Assert.Equal(1f, bottomRight.X, 4);
        Assert.Equal(-1f, bottomRight.Y, 4);

        var center = vp.MulPoint(new Vec3(400f, 300f, 0f));
        Assert.Equal(0f, center.X, 4);
        Assert.Equal(0f, center.Y, 4);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SpriteDraw MakeDraw(uint texture, float colorR = 1f, short layer = 0,
        short order = 0,
        Material2D? material = null)
    {
        return new SpriteDraw {
            Width = 1f,
            Height = 1f,
            PivotX = 0.5f,
            PivotY = 0.5f,
            Frame = SpriteFrame.Full,
            Color = new Vec4(
                colorR,
                1f,
                1f,
                1f
            ),
            SortingLayer = layer,
            OrderInLayer = order,
            Texture = texture,
            Material = material,
        };
    }

    /// <summary>True when the rects overlap or the gap between them is under <paramref name="padding" />.</summary>
    private static bool CloserThanPadding(AtlasSprite a, AtlasSprite b, int padding)
    {
        return a.PixelX < b.PixelX + b.PixelWidth + padding && b.PixelX <
                                                            a.PixelX + a.PixelWidth + padding
                                                            && a.PixelY < b.PixelY + b.PixelHeight +
                                                            padding &&
                                                            b.PixelY < a.PixelY + a.PixelHeight +
                                                            padding;
    }

    private sealed class FakeSpriteDevice : ISpriteDevice
    {
        public readonly List<uint> Destroyed = [];

        public readonly List<SubmitCall> Submits = [];
        private uint _nextHandle = 1;
        public int BeginCalls;
        public int CreateTextureCalls;
        public float[] LastOverlayVp = [];
        public float[] LastSceneVp = [];
        public byte[] LastTexturePixels = [];
        public int LastTextureWidth;
        public float LastViewportH;
        public float LastViewportW;

        public uint CreateTexture(ReadOnlySpan<byte> rgba, int width, int height,
            SpriteFilter filter, bool srgb,
            SpriteWrap wrap)
        {
            CreateTextureCalls++;
            LastTexturePixels = rgba.ToArray(); // spans are transient — copy
            LastTextureWidth = width;
            return _nextHandle++;
        }

        public uint CreateTextureFromFile(string path, SpriteFilter filter, bool srgb,
            SpriteWrap wrap,
            out int width, out int height)
        {
            width = 0;
            height = 0;
            return 0; // no filesystem in headless tests
        }

        public void DestroyTexture(uint texture)
        {
            Destroyed.Add(texture);
        }

        public uint CreateShader(string wgsl)
        {
            return _nextHandle++;
        }

        public void Begin(ReadOnlySpan<float> sceneViewProj, ReadOnlySpan<float> overlayViewProj,
            float viewportW,
            float viewportH)
        {
            BeginCalls++;
            LastSceneVp = sceneViewProj.ToArray();
            LastOverlayVp = overlayViewProj.ToArray();
            LastViewportW = viewportW;
            LastViewportH = viewportH;
        }

        public void Submit(uint texture, uint texture2, uint shader, Blend2D blend, Stage2D stage,
            ReadOnlySpan<float> materialParams, ReadOnlySpan<float> instances, int count)
        {
            Submits.Add(
                new SubmitCall(
                    texture,
                    texture2,
                    shader,
                    blend,
                    stage,
                    materialParams.ToArray(),
                    instances.ToArray(),
                    count
                )
            );
        }

        public readonly record struct SubmitCall(
            uint Texture,
            uint Texture2,
            uint Shader,
            Blend2D Blend,
            Stage2D Stage,
            float[] Params,
            float[] Instances,
            int Count);
    }

    /// <summary>Alloc-free device for the zero-GC frame test.</summary>
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
