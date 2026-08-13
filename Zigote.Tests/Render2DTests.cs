using Xunit;
using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Render2D;

namespace Zigote.Tests;

public class Render2DTests
{
    /// <summary>Floats per packed instance — kept in step with the renderer/shader layout.</summary>
    private const int Stride = ZigoteEngine.SpriteInstanceFloats;

    // ── SpriteSheet.GridFrames ───────────────────────────────────────────────

    [Fact]
    public void GridFrames_PlainGrid_ComputesUvsAndPixelSizes()
    {
        var frames = SpriteSheet.GridFrames(
            texWidth: 64,
            texHeight: 32,
            cols: 4,
            rows: 2
        );

        Assert.Equal(expected: 8, actual: frames.Length);
        Assert.Equal(
            expected: new SpriteFrame(
                U0: 0f,
                V0: 0f,
                U1: 0.25f,
                V1: 0.5f,
                PixelWidth: 16,
                PixelHeight: 16
            ),
            actual: frames[0]
        );
        Assert.Equal(
            expected: new SpriteFrame(
                U0: 0.25f,
                V0: 0f,
                U1: 0.5f,
                V1: 0.5f,
                PixelWidth: 16,
                PixelHeight: 16
            ),
            actual: frames[1]
        );
        Assert.Equal(
            expected: new SpriteFrame(
                U0: 0.75f,
                V0: 0.5f,
                U1: 1f,
                V1: 1f,
                PixelWidth: 16,
                PixelHeight: 16
            ),
            actual: frames[7]
        );
    }

    [Fact]
    public void GridFrames_IsRowMajor()
    {
        var frames = SpriteSheet.GridFrames(
            texWidth: 64,
            texHeight: 32,
            cols: 4,
            rows: 2
        );

        // frames[cols] is the first frame of row 1: same U as frame 0, V shifted a row down.
        Assert.Equal(expected: frames[0].U0, actual: frames[4].U0, precision: 5);
        Assert.Equal(expected: 0.5f, actual: frames[4].V0, precision: 5);
        Assert.Equal(expected: 1f, actual: frames[4].V1, precision: 5);
    }

    [Fact]
    public void GridFrames_MarginAndSpacing_ShiftCells()
    {
        var frames = SpriteSheet.GridFrames(
            texWidth: 70,
            texHeight: 38,
            cols: 2,
            rows: 2,
            marginX: 3,
            marginY: 3,
            spacingX: 4,
            spacingY: 4
        );

        // cellW = (70 - 2*3 - 1*4) / 2 = 30, cellH = (38 - 2*3 - 1*4) / 2 = 14.
        Assert.Equal(expected: 4, actual: frames.Length);
        Assert.Equal(expected: 30, actual: frames[0].PixelWidth);
        Assert.Equal(expected: 14, actual: frames[0].PixelHeight);
        Assert.Equal(expected: 3f / 70f, actual: frames[0].U0, precision: 5);
        Assert.Equal(expected: 3f / 38f, actual: frames[0].V0, precision: 5);
        Assert.Equal(expected: 33f / 70f, actual: frames[0].U1, precision: 5);
        Assert.Equal(expected: 17f / 38f, actual: frames[0].V1, precision: 5);
        Assert.Equal(expected: 37f / 70f, actual: frames[1].U0, precision: 5); // x = 3 + (30 + 4)
        Assert.Equal(expected: 21f / 38f, actual: frames[2].V0, precision: 5); // y = 3 + (14 + 4)
        Assert.Equal(expected: 35f / 38f, actual: frames[2].V1, precision: 5);
    }

    [Fact]
    public void SpriteSheet_Frame_ClampsIndex()
    {
        var device = new FakeSpriteDevice();
        var texture = SpriteTexture.FromPixels(
            device: device,
            rgba: new byte[64 * 32 * 4],
            width: 64,
            height: 32
        )!;
        var sheet = SpriteSheet.FromGrid(texture: texture, cols: 4, rows: 2);

        Assert.Equal(expected: 8, actual: sheet.FrameCount);
        Assert.Equal(expected: sheet.Frames[0], actual: sheet.Frame(-5));
        Assert.Equal(expected: sheet.Frames[7], actual: sheet.Frame(99));
    }

    // ── DynamicTextureAtlas ──────────────────────────────────────────────────

    [Fact]
    public void Atlas_EntriesStayInBounds_AndKeepPaddingApart()
    {
        const int padding = 2;
        using var atlas = new DynamicTextureAtlas(
            device: new NullSpriteDevice(),
            initialSize: 128,
            maxSize: 256
        );

        int[][] sizes =
            [[40, 30], [50, 20], [60, 25], [30, 40], [20, 20], [45, 15], [25, 25], [35, 35]];
        var sprites = new List<AtlasSprite>();
        foreach (int[] s in sizes)
        {
            var sprite = atlas.TryAdd(rgba: new byte[s[0] * s[1] * 4], width: s[0], height: s[1]);
            Assert.NotNull(sprite);
            sprites.Add(sprite);
        }

        foreach (var s in sprites)
        {
            Assert.True(s.PixelX >= 0 && s.PixelY >= 0);
            Assert.True(s.PixelX + s.PixelWidth <= atlas.Size);
            Assert.True(s.PixelY + s.PixelHeight <= atlas.Size);
        }

        for (int i = 0; i < sprites.Count; i++)
        for (int j = i + 1; j < sprites.Count; j++)
        {
            Assert.False(
                condition: CloserThanPadding(a: sprites[i], b: sprites[j], padding: padding),
                userMessage: $"entries {i} and {j} overlap or sit closer than {padding}px"
            );
        }
    }

    [Fact]
    public void Atlas_Growth_RepacksAndRenormalizesExistingFrames()
    {
        using var atlas = new DynamicTextureAtlas(
            device: new NullSpriteDevice(),
            initialSize: 64,
            maxSize: 256
        );

        var a = atlas.TryAdd(rgba: new byte[32 * 32 * 4], width: 32, height: 32)!;
        Assert.Equal(expected: 64, actual: atlas.Size);
        Assert.Equal(
            expected: new SpriteFrame(
                U0: 0f,
                V0: 0f,
                U1: 0.5f,
                V1: 0.5f,
                PixelWidth: 32,
                PixelHeight: 32
            ),
            actual: a.Frame
        );

        // A second 32×32 can't fit at 64 (shelf 2 would start at y=34): grows to 128 and repacks.
        var b = atlas.TryAdd(rgba: new byte[32 * 32 * 4], width: 32, height: 32)!;
        Assert.Equal(expected: 128, actual: atlas.Size);

        // The existing sprite's frame was renormalized against the NEW size...
        Assert.Equal(
            expected: new SpriteFrame(
                U0: 0f,
                V0: 0f,
                U1: 0.25f,
                V1: 0.25f,
                PixelWidth: 32,
                PixelHeight: 32
            ),
            actual: a.Frame
        );
        // ...and the new one sits a padding gap to its right, on the same shelf.
        Assert.Equal(expected: 34, actual: b.PixelX);
        Assert.Equal(expected: 0, actual: b.PixelY);
        Assert.Equal(
            expected: new SpriteFrame(
                U0: 34f / 128f,
                V0: 0f,
                U1: 66f / 128f,
                V1: 0.25f,
                PixelWidth: 32,
                PixelHeight: 32
            ),
            actual: b.Frame
        );
    }

    [Fact]
    public void Atlas_FramesAlwaysMatchPixelRects()
    {
        using var atlas = new DynamicTextureAtlas(
            device: new NullSpriteDevice(),
            initialSize: 64,
            maxSize: 512
        );

        var sprites = new List<AtlasSprite>();
        for (int i = 0; i < 12; i++)
            sprites.Add(atlas.TryAdd(rgba: new byte[48 * 24 * 4], width: 48, height: 24)!);

        float inv = 1f / atlas.Size;
        foreach (var s in sprites)
        {
            Assert.Equal(expected: s.PixelX * inv, actual: s.Frame.U0, precision: 5);
            Assert.Equal(expected: s.PixelY * inv, actual: s.Frame.V0, precision: 5);
            Assert.Equal(
                expected: (s.PixelX + s.PixelWidth) * inv,
                actual: s.Frame.U1,
                precision: 5
            );
            Assert.Equal(
                expected: (s.PixelY + s.PixelHeight) * inv,
                actual: s.Frame.V1,
                precision: 5
            );
        }
    }

    [Fact]
    public void Atlas_TryAdd_TooBigReturnsNull_AndPreservesExistingRects()
    {
        using var atlas = new DynamicTextureAtlas(
            device: new NullSpriteDevice(),
            initialSize: 64,
            maxSize: 64
        );

        var a = atlas.TryAdd(rgba: new byte[40 * 40 * 4], width: 40, height: 40)!;
        var frameBefore = a.Frame;
        int xBefore = a.PixelX;
        int yBefore = a.PixelY;

        // Bigger than maxSize in one dimension: rejected outright.
        Assert.Null(atlas.TryAdd(rgba: new byte[100 * 10 * 4], width: 100, height: 10));
        // Fits dimensionally but not capacity-wise, and the atlas is already at maxSize.
        Assert.Null(atlas.TryAdd(rgba: new byte[40 * 40 * 4], width: 40, height: 40));

        Assert.Equal(expected: frameBefore, actual: a.Frame);
        Assert.Equal(expected: xBefore, actual: a.PixelX);
        Assert.Equal(expected: yBefore, actual: a.PixelY);
        Assert.Equal(expected: 64, actual: atlas.Size);
    }

    [Fact]
    public void Atlas_Commit_UploadsPixels_ReplacesTexture_AndIsNoOpWhenClean()
    {
        var device = new FakeSpriteDevice();
        using var atlas = new DynamicTextureAtlas(device: device, initialSize: 64, maxSize: 64);
        Assert.Equal(expected: 0u, actual: atlas.TextureHandle);

        byte[] rgba = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160];
        var sprite = atlas.TryAdd(rgba: rgba, width: 2, height: 2)!;
        atlas.Commit();

        uint first = atlas.TextureHandle;
        Assert.NotEqual(expected: 0u, actual: first);
        Assert.Equal(expected: 1, actual: device.CreateTextureCalls);
        Assert.Equal(expected: 64, actual: device.LastTextureWidth);

        // The entry's pixels landed at its rect (row 1 starts one atlas stride down).
        int stride = atlas.Size * 4;
        int o = (sprite.PixelY * stride) + (sprite.PixelX * 4);
        Assert.Equal(expected: rgba[..8], actual: device.LastTexturePixels[o..(o + 8)]);
        Assert.Equal(
            expected: rgba[8..],
            actual: device.LastTexturePixels[(o + stride)..(o + stride + 8)]
        );

        atlas.Commit(); // clean → no-op
        Assert.Equal(expected: 1, actual: device.CreateTextureCalls);

        atlas.TryAdd(rgba: new byte[3 * 3 * 4], width: 3, height: 3);
        atlas.Commit(); // dirty → destroys the old texture, creates a new one
        Assert.Equal(expected: 2, actual: device.CreateTextureCalls);
        Assert.Contains(expected: first, collection: device.Destroyed);
        Assert.NotEqual(expected: first, actual: atlas.TextureHandle);
    }

    // ── Renderer2D: sorting ──────────────────────────────────────────────────

    [Fact]
    public void Renderer_EqualKeys_PreserveSubmissionOrder()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);

        renderer.Begin(
            sceneViewProjection: Mat4.Identity,
            overlayViewProjection: Mat4.Identity,
            viewportW: 800f,
            viewportH: 600f
        );
        for (int i = 0; i < 4; i++)
            renderer.Draw(MakeDraw(texture: 7, colorR: i));
        renderer.End();

        var batch = Assert.Single(device.Submits);
        Assert.Equal(expected: 4, actual: batch.Count);
        for (int i = 0; i < 4; i++)
            Assert.Equal(expected: i, actual: batch.Instances[(i * Stride) + 10], precision: 5);
    }

    [Fact]
    public void Renderer_SortsLayersBeforeOrders()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);

        renderer.Begin(
            sceneViewProjection: Mat4.Identity,
            overlayViewProjection: Mat4.Identity,
            viewportW: 800f,
            viewportH: 600f
        );
        renderer.Draw(
            MakeDraw(
                texture: 7,
                colorR: 0,
                layer: 1,
                order: -100
            )
        );
        renderer.Draw(
            MakeDraw(
                texture: 7,
                colorR: 1,
                layer: 0,
                order: 100
            )
        );
        renderer.Draw(
            MakeDraw(
                texture: 7,
                colorR: 2,
                layer: 0,
                order: -5
            )
        );
        renderer.Draw(
            MakeDraw(
                texture: 7,
                colorR: 3,
                layer: -2,
                order: 32767
            )
        );
        renderer.End();

        var batch = Assert.Single(device.Submits);
        float[] expectedOrder = [3f, 2f, 1f, 0f]; // layer -2, then (0,-5), (0,100), then layer 1
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(
                expected: expectedOrder[i],
                actual: batch.Instances[(i * Stride) + 10],
                precision: 5
            );
        }
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
            sceneViewProjection: Mat4.Identity,
            overlayViewProjection: Mat4.Identity,
            viewportW: 800f,
            viewportH: 600f
        );
        renderer.Draw(MakeDraw(1));
        renderer.Draw(MakeDraw(1)); // same key → no split
        renderer.Draw(MakeDraw(2)); // texture change
        renderer.Draw(MakeDraw(texture: 2, material: additive)); // blend change
        renderer.Draw(MakeDraw(texture: 2, material: overlay)); // stage change
        renderer.Draw(MakeDraw(texture: 2, material: cloneOfDefault)); // reference change only
        renderer.End();

        Assert.Equal(expected: 6, actual: renderer.DrawCount);
        Assert.Equal(expected: 5, actual: renderer.BatchCount);
        Assert.Equal(expected: 5, actual: device.Submits.Count);

        Assert.Equal(
            expected: (1u, 2, Blend2D.Alpha, Stage2D.Scene),
            actual: Key(device.Submits[0])
        );
        Assert.Equal(
            expected: (2u, 1, Blend2D.Alpha, Stage2D.Scene),
            actual: Key(device.Submits[1])
        );
        Assert.Equal(
            expected: (2u, 1, Blend2D.Additive, Stage2D.Scene),
            actual: Key(device.Submits[2])
        );
        Assert.Equal(
            expected: (2u, 1, Blend2D.Alpha, Stage2D.Overlay),
            actual: Key(device.Submits[3])
        );
        Assert.Equal(
            expected: (2u, 1, Blend2D.Alpha, Stage2D.Scene),
            actual: Key(device.Submits[4])
        );

        static (uint, int, Blend2D, Stage2D) Key(FakeSpriteDevice.SubmitCall c) =>
            (c.Texture, c.Count, c.Blend, c.Stage);
    }

    [Fact]
    public void Renderer_DoesNotValidateHandles_ZeroTextureStillSubmits()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);

        renderer.Begin(
            sceneViewProjection: Mat4.Identity,
            overlayViewProjection: Mat4.Identity,
            viewportW: 800f,
            viewportH: 600f
        );
        renderer.Draw(MakeDraw(0));
        renderer.End();

        var batch = Assert.Single(device.Submits);
        Assert.Equal(expected: 0u, actual: batch.Texture);
        Assert.Equal(expected: 1, actual: batch.Count);
    }

    [Fact]
    public void Renderer_Begin_ForwardsColumnMajorMatricesAndViewport()
    {
        var device = new FakeSpriteDevice();
        var renderer = new Renderer2D(device);
        var scene = Mat4.Translation(new Vec3(x: 1f, y: 2f, z: 3f));
        var overlayVp = Camera2D.PixelOverlay(viewportW: 320f, viewportH: 240f);

        renderer.Begin(
            sceneViewProjection: scene,
            overlayViewProjection: overlayVp,
            viewportW: 320f,
            viewportH: 240f
        );

        Assert.Equal(expected: 1, actual: device.BeginCalls);
        Assert.Equal(expected: scene.ToArray(), actual: device.LastSceneVp);
        Assert.Equal(expected: overlayVp.ToArray(), actual: device.LastOverlayVp);
        Assert.Equal(expected: 320f, actual: device.LastViewportW);
        Assert.Equal(expected: 240f, actual: device.LastViewportH);
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
                U0: 0.1f,
                V0: 0.2f,
                U1: 0.3f,
                V1: 0.4f,
                PixelWidth: 4,
                PixelHeight: 2
            ),
            Color = new Vec4(
                x: 0.25f,
                y: 0.5f,
                z: 0.75f,
                w: 1f
            ),
            Texture = 1,
        };

        renderer.Begin(
            sceneViewProjection: Mat4.Identity,
            overlayViewProjection: Mat4.Identity,
            viewportW: 800f,
            viewportH: 600f
        );
        renderer.Draw(in draw);
        renderer.End();

        float[] inst = Assert.Single(device.Submits).Instances;
        Assert.Equal(expected: 9f, actual: inst[0], precision: 4);
        Assert.Equal(expected: 22f, actual: inst[1], precision: 4);
        Assert.Equal(expected: 3f, actual: inst[2], precision: 5);
        Assert.Equal(expected: MathF.PI / 2f, actual: inst[3], precision: 5);
        Assert.Equal(expected: 4f, actual: inst[4], precision: 5);
        Assert.Equal(expected: 2f, actual: inst[5], precision: 5);
        Assert.Equal(expected: 0.1f, actual: inst[6], precision: 5);
        Assert.Equal(expected: 0.2f, actual: inst[7], precision: 5);
        Assert.Equal(expected: 0.3f, actual: inst[8], precision: 5);
        Assert.Equal(expected: 0.4f, actual: inst[9], precision: 5);
        Assert.Equal(expected: 0.25f, actual: inst[10], precision: 5);
        Assert.Equal(expected: 0.5f, actual: inst[11], precision: 5);
        Assert.Equal(expected: 0.75f, actual: inst[12], precision: 5);
        Assert.Equal(expected: 1f, actual: inst[13], precision: 5);
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
                U0: 0.1f,
                V0: 0.2f,
                U1: 0.3f,
                V1: 0.4f,
                PixelWidth: 6,
                PixelHeight: 3
            ),
            Color = Vec4.One,
            FlipX = true,
            FlipY = true,
            Texture = 1,
        };

        renderer.Begin(
            sceneViewProjection: Mat4.Identity,
            overlayViewProjection: Mat4.Identity,
            viewportW: 800f,
            viewportH: 600f
        );
        renderer.Draw(in draw);
        renderer.End();

        float[] inst = Assert.Single(device.Submits).Instances;
        Assert.Equal(expected: 5f, actual: inst[0], precision: 5);
        Assert.Equal(expected: -7f, actual: inst[1], precision: 5);
        Assert.Equal(expected: 0.3f, actual: inst[6], precision: 5); // u0 ↔ u1
        Assert.Equal(expected: 0.4f, actual: inst[7], precision: 5); // v0 ↔ v1
        Assert.Equal(expected: 0.1f, actual: inst[8], precision: 5);
        Assert.Equal(expected: 0.2f, actual: inst[9], precision: 5);
    }

    // ── Renderer2D: zero allocation ──────────────────────────────────────────

    [Fact]
    public void Renderer_SecondIdenticalFrame_AllocatesZero()
    {
        var renderer = new Renderer2D(new NullSpriteDevice());
        var shared = new Material2D { Blend = Blend2D.Additive };
        var scene = Mat4.Identity;
        var overlayVp = Camera2D.PixelOverlay(viewportW: 800f, viewportH: 600f);

        // Warm up past tiered JIT and grow every internal buffer to its steady-state size.
        for (int i = 0; i < 200; i++)
        {
            Frame(
                renderer: renderer,
                scene: scene,
                overlayVp: overlayVp,
                shared: shared
            );
        }

        Assert.Equal(expected: 32, actual: renderer.DrawCount);
        Assert.True(renderer.BatchCount > 1);

        const int frames = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            Frame(
                renderer: renderer,
                scene: scene,
                overlayVp: overlayVp,
                shared: shared
            );
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: allocated == 0,
            userMessage: $"Renderer2D frame allocated {allocated} B over {frames} frames " +
                         $"({allocated / (double)frames:F2} B/frame); expected 0."
        );

        static void Frame(Renderer2D renderer, in Mat4 scene, in Mat4 overlayVp, Material2D shared)
        {
            renderer.Begin(
                sceneViewProjection: scene,
                overlayViewProjection: overlayVp,
                viewportW: 800f,
                viewportH: 600f
            );
            for (int i = 0; i < 32; i++)
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
                        U0: 0f,
                        V0: 0f,
                        U1: 1f,
                        V1: 1f,
                        PixelWidth: 32,
                        PixelHeight: 32
                    ),
                    Color = Vec4.One,
                    FlipX = (i & 1) != 0,
                    SortingLayer = (short)(i % 3),
                    OrderInLayer = (short)(i % 5),
                    Texture = (uint)(1 + (i % 2)),
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
            Position = new Vec2(x: 3f, y: 4f),
            Rotation = 0.7f,
            OrthoHeight = 12f,
        };
        var vp = camera.ViewProjection(viewportW: 800f, viewportH: 600f);

        var ndc = vp.MulPoint(new Vec3(x: 3f, y: 4f, z: 0f));
        Assert.Equal(expected: 0f, actual: ndc.X, precision: 4);
        Assert.Equal(expected: 0f, actual: ndc.Y, precision: 4);
        Assert.InRange(actual: ndc.Z, low: 0f, high: 1f); // wgpu 0..1 depth
    }

    [Fact]
    public void Camera_ViewProjection_IsYUp_AndScalesByOrthoHeightAndZoom()
    {
        var camera = new Camera2D { OrthoHeight = 10f };
        var vp = camera.ViewProjection(viewportW: 800f, viewportH: 600f);

        // Half the visible height above center lands on NDC +1 (Y up).
        Assert.Equal(
            expected: 1f,
            actual: vp.MulPoint(new Vec3(x: 0f, y: 5f, z: 0f)).Y,
            precision: 4
        );
        // Half the visible width (halfH · aspect) lands on NDC +1.
        Assert.Equal(
            expected: 1f,
            actual: vp.MulPoint(new Vec3(x: 5f * (800f / 600f), y: 0f, z: 0f)).X,
            precision: 4
        );

        camera.Zoom = 2f; // halves the visible height
        vp = camera.ViewProjection(viewportW: 800f, viewportH: 600f);
        Assert.Equal(
            expected: 1f,
            actual: vp.MulPoint(new Vec3(x: 0f, y: 2.5f, z: 0f)).Y,
            precision: 4
        );
    }

    [Fact]
    public void Camera_Rotation_TurnsTheViewCcw()
    {
        var camera = new Camera2D {
            Rotation = MathF.PI / 2f,
            OrthoHeight = 10f,
        };
        var vp = camera.ViewProjection(viewportW: 600f, viewportH: 600f);

        // Camera rotated 90° CCW: its up axis points at world -X, so (-5, 0) is the top edge.
        var ndc = vp.MulPoint(new Vec3(x: -5f, y: 0f, z: 0f));
        Assert.Equal(expected: 0f, actual: ndc.X, precision: 4);
        Assert.Equal(expected: 1f, actual: ndc.Y, precision: 4);
    }

    [Fact]
    public void Camera_PixelOverlay_MapsTopLeftAndBottomRightCorners()
    {
        var vp = Camera2D.PixelOverlay(viewportW: 800f, viewportH: 600f);

        var topLeft = vp.MulPoint(new Vec3(x: 0f, y: 0f, z: 0f));
        Assert.Equal(expected: -1f, actual: topLeft.X, precision: 4);
        Assert.Equal(expected: 1f, actual: topLeft.Y, precision: 4);

        var bottomRight = vp.MulPoint(new Vec3(x: 800f, y: 600f, z: 0f));
        Assert.Equal(expected: 1f, actual: bottomRight.X, precision: 4);
        Assert.Equal(expected: -1f, actual: bottomRight.Y, precision: 4);

        var center = vp.MulPoint(new Vec3(x: 400f, y: 300f, z: 0f));
        Assert.Equal(expected: 0f, actual: center.X, precision: 4);
        Assert.Equal(expected: 0f, actual: center.Y, precision: 4);
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
                x: colorR,
                y: 1f,
                z: 1f,
                w: 1f
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
        public int BeginCalls;
        public int CreateTextureCalls;
        public float[] LastOverlayVp = [];
        public float[] LastSceneVp = [];
        public byte[] LastTexturePixels = [];
        public int LastTextureWidth;
        public float LastViewportH;
        public float LastViewportW;
        private uint _nextHandle = 1;

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

        public void DestroyTexture(uint texture) => Destroyed.Add(texture);

        public uint CreateShader(string wgsl) => _nextHandle++;

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
                    Texture: texture,
                    Texture2: texture2,
                    Shader: shader,
                    Blend: blend,
                    Stage: stage,
                    Params: materialParams.ToArray(),
                    Instances: instances.ToArray(),
                    Count: count
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
