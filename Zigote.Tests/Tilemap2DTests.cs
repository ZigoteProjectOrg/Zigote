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

        Assert.True(layer.SetTile(x: 3, y: -2, tile: 7));

        Assert.Equal(expected: 3, actual: layer.OriginX);
        Assert.Equal(expected: -2, actual: layer.OriginY);
        Assert.Equal(expected: 1, actual: layer.Width);
        Assert.Equal(expected: 1, actual: layer.Height);
        Assert.Equal(expected: 7, actual: layer.GetTile(x: 3, y: -2));
    }

    [Fact]
    public void SetTile_OutsideRect_GrowsAndPreservesExistingTiles()
    {
        var layer = new TilemapLayer();
        layer.SetTile(x: 0, y: 0, tile: 1);
        layer.SetTile(x: 1, y: 0, tile: 2);
        layer.SetTile(x: 0, y: 1, tile: 3);

        // Grow down-left and up-right; the original three tiles must survive the reallocation.
        layer.SetTile(x: -2, y: -1, tile: 9);
        layer.SetTile(x: 4, y: 3, tile: 8);

        Assert.Equal(expected: 1, actual: layer.GetTile(x: 0, y: 0));
        Assert.Equal(expected: 2, actual: layer.GetTile(x: 1, y: 0));
        Assert.Equal(expected: 3, actual: layer.GetTile(x: 0, y: 1));
        Assert.Equal(expected: 9, actual: layer.GetTile(x: -2, y: -1));
        Assert.Equal(expected: 8, actual: layer.GetTile(x: 4, y: 3));
        Assert.Equal(expected: -2, actual: layer.OriginX);
        Assert.Equal(expected: -1, actual: layer.OriginY);
        Assert.Equal(expected: 7, actual: layer.Width);
        Assert.Equal(expected: 5, actual: layer.Height);
    }

    [Fact]
    public void GetTile_OutsideRect_IsEmptyNotOutOfRange()
    {
        var layer = new TilemapLayer();
        layer.SetTile(x: 0, y: 0, tile: 5);

        Assert.Equal(expected: Tileset.EmptyTile, actual: layer.GetTile(x: 100, y: 100));
        Assert.Equal(expected: Tileset.EmptyTile, actual: layer.GetTile(x: -100, y: -100));
    }

    [Fact]
    public void SetTile_ErasingOutsideRect_DoesNotGrow()
    {
        var layer = new TilemapLayer();
        layer.SetTile(x: 0, y: 0, tile: 1);

        Assert.False(layer.SetTile(x: 50, y: 50, tile: Tileset.EmptyTile));
        Assert.Equal(expected: 1, actual: layer.Width);
        Assert.Equal(expected: 1, actual: layer.Height);
    }

    [Fact]
    public void SetTile_SameValue_ReportsNoChange()
    {
        var layer = new TilemapLayer();
        layer.SetTile(x: 2, y: 2, tile: 4);

        Assert.False(layer.SetTile(x: 2, y: 2, tile: 4));
        Assert.True(layer.SetTile(x: 2, y: 2, tile: 5));
    }

    [Fact]
    public void Trim_ShrinksRectToPaintedTiles()
    {
        var layer = new TilemapLayer();
        layer.SetTile(x: 0, y: 0, tile: 1);
        layer.SetTile(x: 10, y: 10, tile: 2);
        layer.SetTile(x: 10, y: 10, tile: Tileset.EmptyTile);

        Assert.Equal(expected: 11, actual: layer.Width); // still stretched by the erased tile

        layer.Trim();

        Assert.Equal(expected: 1, actual: layer.Width);
        Assert.Equal(expected: 1, actual: layer.Height);
        Assert.Equal(expected: 0, actual: layer.OriginX);
        Assert.Equal(expected: 1, actual: layer.GetTile(x: 0, y: 0));
    }

    [Fact]
    public void Trim_AllErased_ClearsLayer()
    {
        var layer = new TilemapLayer();
        layer.SetTile(x: 4, y: 4, tile: 1);
        layer.SetTile(x: 4, y: 4, tile: Tileset.EmptyTile);

        layer.Trim();

        Assert.True(layer.IsEmpty);
        Assert.Empty(layer.Cells);
    }

    [Fact]
    public void Clone_DeepCopiesCells()
    {
        var layer = new TilemapLayer { Name = "Ground" };
        layer.SetTile(x: 0, y: 0, tile: 3);

        var copy = layer.Clone();
        copy.SetTile(x: 0, y: 0, tile: 9);

        Assert.Equal(expected: 3, actual: layer.GetTile(x: 0, y: 0));
        Assert.Equal(expected: 9, actual: copy.GetTile(x: 0, y: 0));
        Assert.Equal(expected: "Ground", actual: copy.Name);
    }

    // ── Tileset ──────────────────────────────────────────────────────────────

    [Fact]
    public void FitToTexture_DerivesGridAndSizesFlags()
    {
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };

        Assert.True(set.FitToTexture(texWidth: 64, texHeight: 32));

        Assert.Equal(expected: 4, actual: set.Columns);
        Assert.Equal(expected: 2, actual: set.Rows);
        Assert.Equal(expected: 8, actual: set.TileCount);
        Assert.Equal(expected: 8, actual: set.Solid.Length);
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

        Assert.True(set.FitToTexture(texWidth: 74, texHeight: 40));

        Assert.Equal(expected: 4, actual: set.Columns);
        Assert.Equal(expected: 2, actual: set.Rows);
    }

    [Fact]
    public void FitToTexture_TileLargerThanTexture_Fails()
    {
        var set = new Tileset {
            TileWidth = 128,
            TileHeight = 128,
        };

        Assert.False(set.FitToTexture(texWidth: 64, texHeight: 64));
    }

    [Fact]
    public void BuildFrames_MatchesSpriteSheetSlicing()
    {
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(texWidth: 64, texHeight: 32);

        var frames = set.BuildFrames();
        var expected = SpriteSheet.GridFrames(
            texWidth: 64,
            texHeight: 32,
            cols: 4,
            rows: 2
        );

        Assert.Equal(expected: expected, actual: frames);
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
        string dir = TempDir();
        try
        {
            string path = Path.Combine(path1: dir, path2: "tiles.tileset");
            var set = new Tileset {
                TexturePath = "art/tiles.png",
                TileWidth = 8,
                TileHeight = 8,
            };
            set.FitToTexture(texWidth: 32, texHeight: 16);
            set.Solid[2] = true;
            set.OneWay[2] = true;
            set.Save(path);

            var loaded = Tileset.Load(path);

            Assert.Equal(expected: "art/tiles.png", actual: loaded.TexturePath);
            Assert.Equal(expected: 4, actual: loaded.Columns);
            Assert.Equal(expected: 2, actual: loaded.Rows);
            Assert.True(loaded.IsSolid(2));
            Assert.True(loaded.IsOneWay(2));
            Assert.False(loaded.IsSolid(1));
        }
        finally
        {
            Directory.Delete(path: dir, recursive: true);
        }
    }

    // ── Collider baking ──────────────────────────────────────────────────────

    [Fact]
    public void Build_MergesHorizontalSolidRunIntoOneBox()
    {
        var set = SolidTileset();
        var node = TilemapNode(set);
        for (int x = 0; x < 10; x++) node.TilemapLayers[0].SetTile(x: x, y: 0, tile: 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(root: node, world: world, tilesetLoader: _ => set);

        Assert.Equal(expected: 1, actual: world.Count);
    }

    [Fact]
    public void Build_SplitsRunsAtGaps()
    {
        var set = SolidTileset();
        var node = TilemapNode(set);
        var layer = node.TilemapLayers[0];
        layer.SetTile(x: 0, y: 0, tile: 1);
        layer.SetTile(x: 1, y: 0, tile: 1);
        // gap at x=2
        layer.SetTile(x: 3, y: 0, tile: 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(root: node, world: world, tilesetLoader: _ => set);

        Assert.Equal(expected: 2, actual: world.Count);
    }

    [Fact]
    public void Build_DoesNotMergeOneWayWithSolid()
    {
        // tile 1 = solid, tile 2 = solid + one-way. Adjacent, but different semantics.
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(texWidth: 64, texHeight: 16);
        set.Solid[1] = true;
        set.Solid[2] = true;
        set.OneWay[2] = true;

        var node = TilemapNode(set);
        node.TilemapLayers[0].SetTile(x: 0, y: 0, tile: 1);
        node.TilemapLayers[0].SetTile(x: 1, y: 0, tile: 2);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(root: node, world: world, tilesetLoader: _ => set);

        Assert.Equal(expected: 2, actual: world.Count);
    }

    [Fact]
    public void Build_NonSolidTilesProduceNoColliders()
    {
        var set = new Tileset {
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(texWidth: 64, texHeight: 16);

        var node = TilemapNode(set);
        for (int x = 0; x < 4; x++) node.TilemapLayers[0].SetTile(x: x, y: 0, tile: 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(root: node, world: world, tilesetLoader: _ => set);

        Assert.Equal(expected: 0, actual: world.Count);
    }

    [Fact]
    public void Build_MergedRunSpansTheWholeRow()
    {
        var set = SolidTileset();
        var node = TilemapNode(set); // TileWorldSize 1, at the origin
        for (int x = 0; x < 4; x++) node.TilemapLayers[0].SetTile(x: x, y: 0, tile: 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(root: node, world: world, tilesetLoader: _ => set);

        // The run covers x ∈ [0,4], y ∈ [0,1] — probe inside both ends and outside.
        Assert.True(HitsAt(world: world, x: 0.5f, y: 0.5f));
        Assert.True(HitsAt(world: world, x: 3.5f, y: 0.5f));
        Assert.False(HitsAt(world: world, x: 4.5f, y: 0.5f));
        Assert.False(HitsAt(world: world, x: 0.5f, y: 1.5f));
    }

    [Fact]
    public void Build_NodeBoxCollider_UsesWorldPositionAndOffset()
    {
        var node = new SceneNode("Solid") {
            Position = new Vec3(x: 5f, y: 2f, z: 0f),
            Collider2DEnabled = true,
            Collider2DOffset = new Vec2(x: 1f, y: 0f),
            Collider2DSize = new Vec2(x: 0.5f, y: 0.5f),
        };

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(root: node, world: world);

        Assert.Equal(expected: 1, actual: world.Count);
        Assert.True(HitsAt(world: world, x: 6f, y: 2f)); // shifted by the offset
        Assert.False(HitsAt(world: world, x: 3.9f, y: 2f)); // outside the half-extents either way
    }

    [Fact]
    public void Build_HiddenNode_ContributesNothing()
    {
        var node = new SceneNode("Solid") {
            Visible = false,
            Collider2DEnabled = true,
        };

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(root: node, world: world);

        Assert.Equal(expected: 0, actual: world.Count);
    }

    [Fact]
    public void Build_TilemapCollisionOff_ContributesNothing()
    {
        var set = SolidTileset();
        var node = TilemapNode(set);
        node.TilemapCollision = false;
        node.TilemapLayers[0].SetTile(x: 0, y: 0, tile: 1);

        var world = new CollisionWorld2D();
        Scene2DPhysics.Build(root: node, world: world, tilesetLoader: _ => set);

        Assert.Equal(expected: 0, actual: world.Count);
    }

    // ── Draw path + culling ──────────────────────────────────────────────────

    [Fact]
    public void Render_DrawsOneInstancePerPaintedTile_InASingleBatch()
    {
        string dir = TempDir();
        try
        {
            var node = TilemapNodeOnDisk(dir: dir, set: out _);
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 5; x++)
                node.TilemapLayers[0].SetTile(x: x, y: y, tile: 1);

            var device = new CountingSpriteDevice();
            var sprites = new Sprite2DSystem(device);

            // A camera wide enough to see the whole 5×4 map.
            sprites.Render(
                root: node,
                sceneViewProjection: new Camera2D { OrthoHeight = 100f }.ViewProjection(
                    viewportW: 256f,
                    viewportH: 256f
                ),
                viewportW: 256f,
                viewportH: 256f,
                includeScriptQueue: false
            );

            Assert.Equal(expected: 20, actual: device.Instances);
            Assert.Equal(
                expected: 1,
                actual: device.Batches
            ); // one texture + one material ⇒ one draw call
        }
        finally
        {
            Directory.Delete(path: dir, recursive: true);
        }
    }

    [Fact]
    public void Render_CullsTilesOutsideTheCamera()
    {
        string dir = TempDir();
        try
        {
            var node = TilemapNodeOnDisk(dir: dir, set: out _);
            for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
                node.TilemapLayers[0].SetTile(x: x, y: y, tile: 1);

            var device = new CountingSpriteDevice();
            var sprites = new Sprite2DSystem(device);

            // 8 units tall at the origin: only the bottom-left corner of the 100×100 map is visible.
            sprites.Render(
                root: node,
                sceneViewProjection: new Camera2D { OrthoHeight = 8f }.ViewProjection(
                    viewportW: 256f,
                    viewportH: 256f
                ),
                viewportW: 256f,
                viewportH: 256f,
                includeScriptQueue: false
            );

            Assert.True(
                condition: device.Instances < 500,
                userMessage:
                $"expected the camera rect to cull most of the 10 000 tiles, drew {device.Instances}"
            );
            Assert.True(
                condition: device.Instances > 0,
                userMessage: "the visible corner must still draw"
            );
        }
        finally
        {
            Directory.Delete(path: dir, recursive: true);
        }
    }

    [Fact]
    public void Render_HiddenLayer_DrawsNothing()
    {
        string dir = TempDir();
        try
        {
            var node = TilemapNodeOnDisk(dir: dir, set: out _);
            node.TilemapLayers[0].SetTile(x: 0, y: 0, tile: 1);
            node.TilemapLayers[0].Visible = false;

            var device = new CountingSpriteDevice();
            new Sprite2DSystem(device).Render(
                root: node,
                sceneViewProjection: new Camera2D { OrthoHeight = 100f }.ViewProjection(
                    viewportW: 256f,
                    viewportH: 256f
                ),
                viewportW: 256f,
                viewportH: 256f,
                includeScriptQueue: false
            );

            Assert.Equal(expected: 0, actual: device.Instances);
        }
        finally
        {
            Directory.Delete(path: dir, recursive: true);
        }
    }

    [Fact]
    public void Render_MissingTileset_DrawsNothingAndDoesNotThrow()
    {
        var node = new SceneNode(name: "Map", kind: NodeKind.Tilemap) {
            TilesetPath = "/does/not/exist.tileset",
            TilemapLayers = [new TilemapLayer()],
        };
        node.TilemapLayers[0].SetTile(x: 0, y: 0, tile: 1);

        var device = new CountingSpriteDevice();
        new Sprite2DSystem(device).Render(
            root: node,
            sceneViewProjection: new Camera2D().ViewProjection(viewportW: 256f, viewportH: 256f),
            viewportW: 256f,
            viewportH: 256f,
            includeScriptQueue: false
        );

        Assert.Equal(expected: 0, actual: device.Instances);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Is any collider covering this point? A tiny probe box stands in for a point query.</summary>
    private static bool HitsAt(CollisionWorld2D world, float x, float y)
    {
        var hits = new List<ColliderHandle>();
        return world.OverlapBox(
            center: new Vec2(x: x, y: y),
            halfExtents: new Vec2(x: 1e-3f, y: 1e-3f),
            mask: uint.MaxValue,
            results: hits
        ) > 0;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "zigote-tilemap-" + Guid.NewGuid().ToString("N")
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
        set.FitToTexture(texWidth: 64, texHeight: 16);
        set.Solid[1] = true;
        return set;
    }

    private static SceneNode TilemapNode(Tileset set)
    {
        return new SceneNode(name: "Map", kind: NodeKind.Tilemap) {
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
        string texPath = Path.Combine(path1: dir, path2: "tiles.png");
        File.WriteAllBytes(path: texPath, bytes: [0]); // the fake device never decodes it
        string setPath = Path.Combine(path1: dir, path2: "tiles.tileset");
        set = new Tileset {
            TexturePath = texPath,
            TileWidth = 16,
            TileHeight = 16,
        };
        set.FitToTexture(texWidth: 64, texHeight: 16);
        set.Save(setPath);

        return new SceneNode(name: "Map", kind: NodeKind.Tilemap) {
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
            bool srgb, SpriteWrap wrap) =>
            1;

        public uint CreateTextureFromFile(string path, SpriteFilter filter, bool srgb,
            SpriteWrap wrap,
            out int width, out int height)
        {
            width = 64;
            height = 16;
            return 1;
        }

        public void DestroyTexture(uint texture) { }

        public uint CreateShader(string wgsl) => 1;

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
