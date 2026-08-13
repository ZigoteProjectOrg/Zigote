using System.Text.Json.Serialization;
using Zigote.Render2D;

namespace Zigote.Runtime.Scene;

/// <summary>
///     One layer of a <see cref="NodeKind.Tilemap" /> node: a dense rectangle of tile indices in
///     tile coordinates, anchored at (<see cref="OriginX" />, <see cref="OriginY" />).
///     <para>
///         Dense rather than sparse on purpose — a tilemap is overwhelmingly filled inside its own
///         bounds, an <c>int[]</c> serializes and iterates without per-cell overhead, and painting
///         outside the current rect simply grows it (<see cref="SetTile" />). Tile coordinates are
///         Y-up to match the 2D world axes: +Y is one tile up on screen.
///     </para>
///     <para>
///         Cell values index the node's tileset; <see cref="Tileset.EmptyTile" /> (-1) is a hole.
///     </para>
/// </summary>
public sealed class TilemapLayer
{
    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;

    /// <summary>Multiplies the tilemap's tint alpha for this layer.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>Draw order, same semantics as the sprite sorting layer / order-in-layer pair.</summary>
    public int SortingLayer { get; set; }

    public int OrderInLayer { get; set; }

    /// <summary>Tile coordinate of <see cref="Cells" />[0] — the rect's lower-left corner.</summary>
    public int OriginX { get; set; }

    public int OriginY { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Row-major, <see cref="Width" />×<see cref="Height" />, row 0 = <see cref="OriginY" />.</summary>
    public int[] Cells { get; set; } = [];

    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Tile at a world tile coordinate; <see cref="Tileset.EmptyTile" /> when outside the rect.</summary>
    public int GetTile(int x, int y)
    {
        int lx = x - OriginX;
        int ly = y - OriginY;
        if (lx < 0 || ly < 0 || lx >= Width || ly >= Height) return Tileset.EmptyTile;
        return Cells[(ly * Width) + lx];
    }

    /// <summary>
    ///     Paint a tile, growing the rect to include it. Erasing (<see cref="Tileset.EmptyTile" />)
    ///     outside the current rect is a no-op rather than a grow — clearing nothing must not allocate.
    ///     Returns true when the layer actually changed.
    /// </summary>
    public bool SetTile(int x, int y, int tile)
    {
        int lx = x - OriginX;
        int ly = y - OriginY;
        if (lx < 0 || ly < 0 || lx >= Width || ly >= Height)
        {
            if (tile == Tileset.EmptyTile) return false;
            GrowToInclude(x: x, y: y);
            lx = x - OriginX;
            ly = y - OriginY;
        }

        int i = (ly * Width) + lx;
        if (Cells[i] == tile) return false;
        Cells[i] = tile;
        return true;
    }

    /// <summary>Reallocate the rect so it covers the given tile coordinate, preserving contents.</summary>
    private void GrowToInclude(int x, int y)
    {
        if (IsEmpty)
        {
            OriginX = x;
            OriginY = y;
            Width = 1;
            Height = 1;
            Cells = [Tileset.EmptyTile];
            return;
        }

        int minX = Math.Min(val1: OriginX, val2: x);
        int minY = Math.Min(val1: OriginY, val2: y);
        int maxX = Math.Max(val1: OriginX + Width - 1, val2: x);
        int maxY = Math.Max(val1: OriginY + Height - 1, val2: y);
        int w = maxX - minX + 1;
        int h = maxY - minY + 1;

        int[] cells = new int[w * h];
        Array.Fill(array: cells, value: Tileset.EmptyTile);
        int dx = OriginX - minX;
        int dy = OriginY - minY;
        for (int row = 0; row < Height; row++)
        {
            Array.Copy(
                sourceArray: Cells,
                sourceIndex: row * Width,
                destinationArray: cells,
                destinationIndex: ((row + dy) * w) + dx,
                length: Width
            );
        }

        Cells = cells;
        OriginX = minX;
        OriginY = minY;
        Width = w;
        Height = h;
    }

    /// <summary>Drop every tile (and the backing array) without disturbing layer settings.</summary>
    public void Clear()
    {
        Cells = [];
        Width = 0;
        Height = 0;
        OriginX = 0;
        OriginY = 0;
    }

    /// <summary>
    ///     Shrink the rect to the painted tiles. Painting then erasing across a wide area leaves the
    ///     rect stretched; the editor trims after bulk erases so the map does not stay large forever.
    /// </summary>
    public void Trim()
    {
        if (IsEmpty) return;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int ly = 0; ly < Height; ly++)
        for (int lx = 0; lx < Width; lx++)
        {
            if (Cells[(ly * Width) + lx] == Tileset.EmptyTile) continue;
            if (lx < minX) minX = lx;
            if (lx > maxX) maxX = lx;
            if (ly < minY) minY = ly;
            if (ly > maxY) maxY = ly;
        }

        if (minX > maxX)
        {
            Clear();
            return;
        }

        if (minX == 0 && minY == 0 && maxX == Width - 1 && maxY == Height - 1) return;

        int w = maxX - minX + 1;
        int h = maxY - minY + 1;
        int[] cells = new int[w * h];
        for (int row = 0; row < h; row++)
        {
            Array.Copy(
                sourceArray: Cells,
                sourceIndex: ((row + minY) * Width) + minX,
                destinationArray: cells,
                destinationIndex: row * w,
                length: w
            );
        }

        Cells = cells;
        OriginX += minX;
        OriginY += minY;
        Width = w;
        Height = h;
    }

    public TilemapLayer Clone()
    {
        return new TilemapLayer {
            Name = Name,
            Visible = Visible,
            Opacity = Opacity,
            SortingLayer = SortingLayer,
            OrderInLayer = OrderInLayer,
            OriginX = OriginX,
            OriginY = OriginY,
            Width = Width,
            Height = Height,
            Cells = (int[])Cells.Clone(),
        };
    }
}
