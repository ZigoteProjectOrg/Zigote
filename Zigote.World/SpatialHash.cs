using Zigote.Core.Math3D;

namespace Zigote.World;

/// <summary>
///     Uniform-grid spatial index over integer entity ids. The play-mode backend rebuilds it lazily
///     (once per tick, on the first spatial query) from the live entities' world positions, so a tick
///     that never queries pays nothing. Cell lists are retained across <see cref="Clear" /> — steady-state
///     rebuild + query allocate nothing once the grid has seen the world's extent.
/// </summary>
public sealed class SpatialHash(float cellSize = 4f)
{
    // Cell entries carry the position alongside the id so Query stays inside the cell list —
    // no dictionary lookup per candidate. _positions only backs TryGetPosition.
    private readonly Dictionary<long, List<(int Id, Vec3 Pos)>> _cells = new();
    private readonly float _cellSize = cellSize > 0f ? cellSize : 4f;
    private readonly Dictionary<int, Vec3> _positions = new();

    public int Count => _positions.Count;

    public void Clear()
    {
        foreach (var list in _cells.Values) list.Clear();
        _positions.Clear();
    }

    public void Insert(int id, Vec3 position)
    {
        _positions[id] = position;
        var key = KeyOf(CellOf(position.X), CellOf(position.Y), CellOf(position.Z));
        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
        list.Add((id, position));
    }

    public bool TryGetPosition(int id, out Vec3 position)
    {
        return _positions.TryGetValue(id, out position);
    }

    /// <summary>
    ///     All ids within <paramref name="radius" /> of <paramref name="center" /> (inclusive), appended
    ///     to <paramref name="results" /> after clearing it. Returns the count.
    /// </summary>
    public int Query(Vec3 center, float radius, List<int> results)
    {
        results.Clear();
        if (radius < 0f || _positions.Count == 0) return 0;

        var r2 = radius * radius;
        int minX = CellOf(center.X - radius), maxX = CellOf(center.X + radius);
        int minY = CellOf(center.Y - radius), maxY = CellOf(center.Y + radius);
        int minZ = CellOf(center.Z - radius), maxZ = CellOf(center.Z + radius);

        for (var cx = minX; cx <= maxX; cx++)
        for (var cy = minY; cy <= maxY; cy++)
        for (var cz = minZ; cz <= maxZ; cz++)
        {
            if (!_cells.TryGetValue(KeyOf(cx, cy, cz), out var list)) continue;
            for (var i = 0; i < list.Count; i++)
            {
                var (id, pos) = list[i];
                var d = pos - center;
                if (d.LengthSq() <= r2) results.Add(id);
            }
        }

        return results.Count;
    }

    private int CellOf(float v)
    {
        return (int)MathF.Floor(v / _cellSize);
    }

    // 21 signed bits per axis (±1,048,575 cells ≈ ±4,194 km at the 4 m default) packed into one long key.
    private static long KeyOf(int x, int y, int z)
    {
        const long mask = (1L << 21) - 1;
        return ((x & mask) << 42) | ((y & mask) << 21) | (z & mask);
    }
}