using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zigote.Render2D;

/// <summary>
///     A <c>.tileset</c> asset: one texture sliced into a uniform grid of tiles, plus the per-tile
///     authoring flags a tilemap needs (solid / one-way for collision baking).
///     <para>
///         Deliberately a plain data record with no GPU handles — a tileset can be loaded, inspected
///         and baked into colliders headlessly (tests, game export, a server build). The texture is
///         resolved separately through <see cref="ISpriteDevice" /> by whoever draws it.
///     </para>
///     <para>
///         Tile indices are row-major, 0-based; <see cref="EmptyTile" /> (-1) means "no tile" and is
///         what an unpainted tilemap cell holds.
///     </para>
/// </summary>
public sealed class Tileset
{
    /// <summary>The cell value meaning "nothing painted here".</summary>
    public const int EmptyTile = -1;

    /// <summary>Texture path, relative to the project root (same convention as node TexturePath).</summary>
    public string TexturePath { get; set; } = "";

    /// <summary>Tile cell size in texture pixels.</summary>
    public int TileWidth { get; set; } = 16;

    public int TileHeight { get; set; } = 16;

    /// <summary>Border skipped on each edge of the sheet, in pixels.</summary>
    public int MarginX { get; set; }

    public int MarginY { get; set; }

    /// <summary>Gap between adjacent tiles, in pixels.</summary>
    public int SpacingX { get; set; }

    public int SpacingY { get; set; }

    public int Columns { get; set; } = 1;
    public int Rows { get; set; } = 1;

    /// <summary>
    ///     Source texture size in pixels, captured when the tileset was authored. Stored rather than
    ///     derived so UVs are exact even when the sheet has trailing padding, and so frame math works
    ///     with no GPU present. Zero falls back to the size implied by the grid.
    /// </summary>
    public int TextureWidth { get; set; }

    public int TextureHeight { get; set; }

    /// <summary>
    ///     Per-tile collision flag, indexed by tile index. Short arrays read as "false" past the end,
    ///     so a hand-written tileset may omit trailing entries.
    /// </summary>
    public bool[] Solid { get; set; } = [];

    /// <summary>Per-tile one-way (jump-through) flag; only meaningful where <see cref="Solid" />.</summary>
    public bool[] OneWay { get; set; } = [];

    /// <summary>Total tiles in the grid.</summary>
    [JsonIgnore]
    public int TileCount => Math.Max(0, Columns) * Math.Max(0, Rows);

    /// <summary>Texture width to slice against — the stored size, or the size the grid implies.</summary>
    [JsonIgnore]
    public int EffectiveTextureWidth => TextureWidth > 0
        ? TextureWidth
        : 2 * MarginX + Columns * TileWidth + Math.Max(0, Columns - 1) * SpacingX;

    [JsonIgnore]
    public int EffectiveTextureHeight => TextureHeight > 0
        ? TextureHeight
        : 2 * MarginY + Rows * TileHeight + Math.Max(0, Rows - 1) * SpacingY;

    public bool IsSolid(int tile)
    {
        return tile >= 0 && tile < Solid.Length && Solid[tile];
    }

    public bool IsOneWay(int tile)
    {
        return tile >= 0 && tile < OneWay.Length && OneWay[tile];
    }

    /// <summary>
    ///     Grow <see cref="Solid" />/<see cref="OneWay" /> to cover every tile so the editor can write
    ///     any index. Called on load and whenever the grid is resized.
    /// </summary>
    public void EnsureFlagCapacity()
    {
        var n = TileCount;
        if (Solid.Length < n)
        {
            var solid = Solid;
            Array.Resize(ref solid, n);
            Solid = solid;
        }

        if (OneWay.Length < n)
        {
            var oneWay = OneWay;
            Array.Resize(ref oneWay, n);
            OneWay = oneWay;
        }
    }

    /// <summary>
    ///     UV frames for every tile, row-major. Delegates the grid math to
    ///     <see cref="SpriteSheet.GridFrames" /> — the same slicing sprite sheets use, so a tileset and
    ///     a sprite sheet over the same image agree exactly.
    /// </summary>
    public SpriteFrame[] BuildFrames()
    {
        return SpriteSheet.GridFrames(
            EffectiveTextureWidth,
            EffectiveTextureHeight,
            Math.Max(1, Columns),
            Math.Max(1, Rows),
            MarginX,
            MarginY,
            SpacingX,
            SpacingY
        );
    }

    /// <summary>
    ///     Fit the grid to a texture of the given pixel size, keeping tile size/margin/spacing.
    ///     Returns false when the tile size cannot produce at least one column and row.
    /// </summary>
    public bool FitToTexture(int texWidth, int texHeight)
    {
        if (texWidth <= 0 || texHeight <= 0 || TileWidth <= 0 || TileHeight <= 0) return false;
        var cols = (texWidth - 2 * MarginX + SpacingX) / (TileWidth + SpacingX);
        var rows = (texHeight - 2 * MarginY + SpacingY) / (TileHeight + SpacingY);
        if (cols <= 0 || rows <= 0) return false;

        TextureWidth = texWidth;
        TextureHeight = texHeight;
        Columns = cols;
        Rows = rows;
        EnsureFlagCapacity();
        return true;
    }

    public static Tileset Load(string path)
    {
        var set = JsonSerializer.Deserialize(File.ReadAllText(path), TilesetJson.Default.Tileset)
                  ?? new Tileset();
        set.EnsureFlagCapacity();
        return set;
    }

    public void Save(string path)
    {
        EnsureFlagCapacity();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, TilesetJson.Indented.Tileset));
    }
}

/// <summary>Source-generated metadata so tilesets load under NativeAOT (no reflection resolver).</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Tileset))]
internal partial class TilesetJson : JsonSerializerContext
{
    private static TilesetJson? _indented;

    public static TilesetJson Indented => _indented ??=
        new TilesetJson(new JsonSerializerOptions { WriteIndented = true });
}
