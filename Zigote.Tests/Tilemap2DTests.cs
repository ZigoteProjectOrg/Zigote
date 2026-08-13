using Xunit;
using Zigote.Core.Math3D;
using Zigote.Physics2D;
using Zigote.Render2D;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

/// <summary>
///     The 2D tilemap stack: layer grid edits, tileset slicing/round-trip, collider baking, and the
///     draw path through <see cref="Sprite2DSystem" /> (including camera culling).
/// </summary>
public class Tilemap2DTests
{
    // ── TilemapLayer ─────────────────────────────────────────────────────────

    [Fact]
    public void SetTile_OnEmptyLayer_SeedsRectAtThatCoordinate()
    {
        var layer = new TilemapLayer();

        Assert.True(layer.SetTile(3, -2, 7));

        Assert.Equal(3, layer.OriginX);
        Assert.Equal(-2, layer.OriginY);
        Assert.Equal(1, layer.Width);
        Assert.Equal(1, layer.Height);
        Assert.Equal(7, layer.GetTile(3, -2));
    }

    [Fact]
    public void SetTile_OutsideRect_GrowsAndPreservesExistingTiles()
    {
        var layer = new TilemapLayer();
        layer.SetTile(0, 0, 1);
        layer.SetTile(1, 0, 2);
        layer.SetTile(0, 1, 3);

        // Grow down-left and up-right; the original three tiles must survive the reallocation.
        layer.SetTile(-2, -1, 9);
        layer.SetTile(4, 3, 8);

        Assert.Equal(1, layer.GetTile(0, 0));
        Assert.Equal(2, layer.GetTile(1, 0));
        Assert.Equal(3, layer.GetTile(0, 1));
        Assert.Equal(9, layer.GetTile(-2, -1));
        Assert.Equal(8, layer.GetTile(4, 3));
        Assert.Equal(-2, layer.OriginX);
        Assert.Equal(-1, layer.OriginY);
        Assert.Equal(7, layer.Width);
        Assert.Equal(5, layer.Height);
    }

    [Fact]
    public void GetTile_OutsideRect_IsEmptyNotOutOfRange()
    {
        var layer = new TilemapLayer();
        layer.SetTile(0, 0, 5);

        Assert.Equal(Tileset.EmptyTile, layer.GetTile(100, 100));
        Assert.Equal(Tileset.EmptyTile, layer.GetTile(-100, -100));
    }

    [Fact]
    public void SetTile_ErasingOutsideRect_DoesNotGrow()
    {
        var layer = new TilemapLayer();
        layer.SetTile(0, 0, 1);

        Assert.False(layer.SetTile(50, 50, Tileset.EmptyTile));
        Assert.Equal(1, layer.Width);
        Assert.Equal(1, layer.Height);
    }

    [Fact]
    public void SetTile_SameValue_ReportsNoChange()
    {
        var layer = new TilemapLayer();
        layer.SetTile(2, 2, 4);

        Assert.False(layer.SetTile(2, 2, 4));
        Assert.True(layer.SetTile(2, 2, 5));
    }

    [Fact]
    public void Trim_ShrinksRectToPaintedTiles()
    {
        var layer = new TilemapLayer();
        layer.SetTile(0, 0, 1);
        layer.SetTile(10, 10, 2);
        layer.SetTile(10, 10, Tileset.EmptyTile);

        Assert.Equal(11, layer.Width); // still stretched by the erased tile

        layer.Trim();

        Assert.Equal(1, layer.Width);
        Assert.Equal(1, layer.Height);
        Assert.Equal(0, layer.OriginX);
        Assert.Equal(1, layer.GetTile(0, 0));
    }

    [Fact]
    public void Trim_AllErased_ClearsLayer()
    {
        var layer = new TilemapLayer();
        layer.SetTile(4, 4, 1);
        layer.SetTile(4, 4, Tileset.EmptyTile);

        layer.Trim();

        Assert.True(layer.IsEmpty);
        Assert.Empty(layer.Cells);
    }

    [Fact]
    public void Clone_DeepCopiesCells()
    {
        var layer = new TilemapLayer { Name = "Ground" };
        layer.SetTile(0, 0, 3);

        var copy = layer.Clone();
        copy.SetTile(0, 0, 9);

        Assert.Equal(3, layer.GetTile(0, 0));
        Assert.Equal(9, copy.GetTile(0, 0));
        Assert.Equal("Ground", copy.Name);
    }

    // ── Tileset ──────────────────────────────────────────────────────────────

    [Fact]
    public void FitToTexture_DerivesGridAndSizesFlags()
    {
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };

        Assert.True(set.FitToTexture(64, 32));

        Assert.Equal(4, set.Columns);
        Assert.Equal(2, set.Rows);
        Assert.Equal(8, set.TileCount);
        Assert.Equal(8, set.Solid.Length);
    }

    [Fact]
    public void FitToTexture_AccountsForMarginAndSpacing()
    {
        // 2px border + 4 tiles of 16 + 3 gaps of 2 = 4 + 64 + 6 = 74 wide.
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
            MarginX = 2,
            MarginY = 2,
            SpacingX = 2,
            SpacingY = 2,
        };

        Assert.True(set.FitToTexture(74, 40));

        Assert.Equal(4, set.Columns);
        Assert.Equal(2, set.Rows);
    }

    [Fact]
    public void FitToTexture_TileLargerThanTexture_Fails()
    {
        var set = new Tileset {
            TileWidth = 128,
            TileHeight = 128,
        };

        Assert.False(set.FitToTexture(64, 64));
    }

    [Fact]
    public void BuildFrames_MatchesSpriteSheetSlicing()
    {
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(64, 32);

        var frames = set.BuildFrames();
        var expected = SpriteSheet.GridFrames(
            64,
            32,
            4,
            2
        );

        Assert.Equal(expected, frames);
    }

    [Fact]
    public void IsSolid_PastFlagArray_IsFalseNotOutOfRange()
    {
        var set = new Tileset { Solid = [true] };

        Assert.True(set.IsSolid(0));
        Assert.False(set.IsSolid(50));
        Assert.False(set.IsSolid(Tileset.EmptyTile));
    }

    [Fact]
    public void SaveLoad_RoundTripsGridAndFlags()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "tiles.tileset");
            var set = new Tileset {
                TexturePath = "art/tiles.png",
                TileWidth = 8,
                TileHeight = 8,
            };
            set.FitToTexture(32, 16);
            set.Solid[2] = true;
            set.OneWay[2] = true;
            set.Save(path);

            var loaded = Tileset.Load(path);

            Assert.Equal("art/tiles.png", loaded.TexturePath);
            Assert.Equal(4, loaded.Columns);
            Assert.Equal(2, loaded.Rows);
            Assert.True(loaded.IsSolid(2));
            Assert.True(loaded.IsOneWay(2));
            Assert.False(loaded.IsSolid(1));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ── Collider baking ──────────────────────────────────────────────────────

    [Fact]
    public void Build_MergesHorizontalSolidRunIntoOneBox()
    {
        var set = SolidTileset();
        var node = TilemapNode(set);
        for (var x = 0; x < 10; x++) node.TilemapLayers[0].SetTile(x, 0, 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(node, world, _ => set);

        Assert.Equal(1, world.Count);
    }

    [Fact]
    public void Build_SplitsRunsAtGaps()
    {
        var set = SolidTileset();
        var node = TilemapNode(set);
        var layer = node.TilemapLayers[0];
        layer.SetTile(0, 0, 1);
        layer.SetTile(1, 0, 1);
        // gap at x=2
        layer.SetTile(3, 0, 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(node, world, _ => set);

        Assert.Equal(2, world.Count);
    }

    [Fact]
    public void Build_DoesNotMergeOneWayWithSolid()
    {
        // tile 1 = solid, tile 2 = solid + one-way. Adjacent, but different semantics.
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(64, 16);
        set.Solid[1] = true;
        set.Solid[2] = true;
        set.OneWay[2] = true;

        var node = TilemapNode(set);
        node.TilemapLayers[0].SetTile(0, 0, 1);
        node.TilemapLayers[0].SetTile(1, 0, 2);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(node, world, _ => set);

        Assert.Equal(2, world.Count);
    }

    [Fact]
    public void Build_NonSolidTilesProduceNoColliders()
    {
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(64, 16);

        var node = TilemapNode(set);
        for (var x = 0; x < 4; x++) node.TilemapLayers[0].SetTile(x, 0, 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(node, world, _ => set);

        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void Build_MergedRunSpansTheWholeRow()
    {
        var set = SolidTileset();
        var node = TilemapNode(set); // TileWorldSize 1, at the origin
        for (var x = 0; x < 4; x++) node.TilemapLayers[0].SetTile(x, 0, 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(node, world, _ => set);

        // The run covers x ∈ [0,4], y ∈ [0,1] — probe inside both ends and outside.
        Assert.True(HitsAt(world, 0.5f, 0.5f));
        Assert.True(HitsAt(world, 3.5f, 0.5f));
        Assert.False(HitsAt(world, 4.5f, 0.5f));
        Assert.False(HitsAt(world, 0.5f, 1.5f));
    }

    [Fact]
    public void Build_NodeBoxCollider_UsesWorldPositionAndOffset()
    {
        var node = new SceneNode("Solid") {
            Position = new Vec3(5f, 2f, 0f),
            Collider2DEnabled = true,
            Collider2DOffset = new Vec2(1f, 0f),
            Collider2DSize = new Vec2(0.5f, 0.5f),
        };

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(node, world);

        Assert.Equal(1, world.Count);
        Assert.True(HitsAt(world, 6f, 2f)); // shifted by the offset
        Assert.False(HitsAt(world, 3.9f, 2f)); // outside the half-extents either way
    }

    [Fact]
    public void Build_HiddenNode_ContributesNothing()
    {
        var node = new SceneNode("Solid") {
            Visible = false,
            Collider2DEnabled = true,
        };

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(node, world);

        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void Build_TilemapCollisionOff_ContributesNothing()
    {
        var set = SolidTileset();
        var node = TilemapNode(set);
        node.TilemapCollision = false;
        node.TilemapLayers[0].SetTile(0, 0, 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(node, world, _ => set);

        Assert.Equal(0, world.Count);
    }

    // ── Draw path + culling ──────────────────────────────────────────────────

    [Fact]
    public void Render_DrawsOneInstancePerPaintedTile_InASingleBatch()
    {
        var dir = TempDir();
        try
        {
            var node = TilemapNodeOnDisk(dir, out _);
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 5; x++)
                node.TilemapLayers[0].SetTile(x, y, 1);

            var device = new CountingSpriteDevice();
            var sprites = new Sprite2DSystem(device);

            // A camera wide enough to see the whole 5×4 map.
            sprites.Render(
                node,
                new Camera2D { OrthoHeight = 100f }.ViewProjection(256f, 256f),
                256f,
                256f,
                false
            );

            Assert.Equal(20, device.Instances);
            Assert.Equal(1, device.Batches); // one texture + one material ⇒ one draw call
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Render_CullsTilesOutsideTheCamera()
    {
        var dir = TempDir();
        try
        {
            var node = TilemapNodeOnDisk(dir, out _);
            for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                node.TilemapLayers[0].SetTile(x, y, 1);

            var device = new CountingSpriteDevice();
            var sprites = new Sprite2DSystem(device);

            // 8 units tall at the origin: only the bottom-left corner of the 100×100 map is visible.
            sprites.Render(
                node,
                new Camera2D { OrthoHeight = 8f }.ViewProjection(256f, 256f),
                256f,
                256f,
                false
            );

            Assert.True(
                device.Instances < 500,
                $"expected the camera rect to cull most of the 10 000 tiles, drew {device.Instances}"
            );
            Assert.True(device.Instances > 0, "the visible corner must still draw");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Render_HiddenLayer_DrawsNothing()
    {
        var dir = TempDir();
        try
        {
            var node = TilemapNodeOnDisk(dir, out _);
            node.TilemapLayers[0].SetTile(0, 0, 1);
            node.TilemapLayers[0].Visible = false;

            var device = new CountingSpriteDevice();
            new Sprite2DSystem(device).Render(
                node,
                new Camera2D { OrthoHeight = 100f }.ViewProjection(256f, 256f),
                256f,
                256f,
                false
            );

            Assert.Equal(0, device.Instances);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Render_MissingTileset_DrawsNothingAndDoesNotThrow()
    {
        var node = new SceneNode("Map", NodeKind.Tilemap) {
            TilesetPath = "/does/not/exist.tileset",
            TilemapLayers = [new TilemapLayer()],
        };
        node.TilemapLayers[0].SetTile(0, 0, 1);

        var device = new CountingSpriteDevice();
        new Sprite2DSystem(device).Render(
            node,
            new Camera2D().ViewProjection(256f, 256f),
            256f,
            256f,
            false
        );

        Assert.Equal(0, device.Instances);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Is any collider covering this point? A tiny probe box stands in for a point query.</summary>
    private static bool HitsAt(CollisionWorld2D world, float x, float y)
    {
        var hits = new List<ColliderHandle>();
        return world.OverlapBox(
            new Vec2(x, y),
            new Vec2(1e-3f, 1e-3f),
            uint.MaxValue,
            hits
        ) > 0;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "zigote-tilemap-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Tileset SolidTileset()
    {
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(64, 16);
        set.Solid[1] = true;
        return set;
    }

    private static SceneNode TilemapNode(Tileset set)
    {
        return new SceneNode("Map", NodeKind.Tilemap) {
            TilesetPath = "unused.tileset", // resolved by the injected loader
            TileWorldSize = 1f,
            TilemapLayers = [new TilemapLayer { Name = "Ground" }],
        };
    }

    /// <summary>
    ///     A tilemap whose tileset and texture actually exist on disk — <see cref="Sprite2DSystem" />
    ///     checks for the files before asking the device to decode them.
    /// </summary>
    private static SceneNode TilemapNodeOnDisk(string dir, out Tileset set)
    {
        var texPath = Path.Combine(dir, "tiles.png");
        File.WriteAllBytes(texPath, [0]); // the fake device never decodes it
        var setPath = Path.Combine(dir, "tiles.tileset");
        set = new Tileset {
            TexturePath = texPath,
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(64, 16);
        set.Save(setPath);

        return new SceneNode("Map", NodeKind.Tilemap) {
            TilesetPath = setPath,
            TileWorldSize = 1f,
            TilemapLayers = [new TilemapLayer { Name = "Ground" }],
        };
    }

    /// <summary>Counts what reached the device, so tests can assert instances and batch collapsing.</summary>
    private sealed class CountingSpriteDevice : ISpriteDevice
    {
        public int Instances { get; private set; }
        public int Batches { get; private set; }

        public uint CreateTexture(ReadOnlySpan<byte> rgba, int width, int height,
            SpriteFilter filter,
            bool srgb, SpriteWrap wrap)
        {
            return 1;
        }

        public uint CreateTextureFromFile(string path, SpriteFilter filter, bool srgb,
            SpriteWrap wrap,
            out int width, out int height)
        {
            width = 64;
            height = 16;
            return 1;
        }

        public void DestroyTexture(uint texture)
        {
        }

        public uint CreateShader(string wgsl)
        {
            return 1;
        }

        public void Begin(ReadOnlySpan<float> sceneViewProj, ReadOnlySpan<float> overlayViewProj,
            float viewportW, float viewportH)
        {
            Instances = 0;
            Batches = 0;
        }

        public void Submit(uint texture, uint texture2, uint shader, Blend2D blend, Stage2D stage,
            ReadOnlySpan<float> materialParams, ReadOnlySpan<float> instances, int count)
        {
            Instances += count;
            Batches++;
        }
    }
}
