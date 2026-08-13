using Xunit;
using Zigote.Core.Math3D;
using Zigote.World;

namespace Zigote.Tests;

public class SpatialHashTests
{
    [Fact]
    public void Query_ReturnsOnlyIdsWithinRadius()
    {
        var hash = new SpatialHash();
        hash.Insert(1, new Vec3(0f, 0f, 0f));
        hash.Insert(2, new Vec3(3f, 0f, 0f));
        hash.Insert(3, new Vec3(0f, 10f, 0f));

        var results = new List<int>();
        var count = hash.Query(Vec3.Zero, 5f, results);

        Assert.Equal(2, count);
        Assert.Contains(1, results);
        Assert.Contains(2, results);
        Assert.DoesNotContain(3, results);
    }

    [Fact]
    public void Query_RadiusIsInclusive()
    {
        var hash = new SpatialHash();
        hash.Insert(1, new Vec3(4f, 0f, 0f));

        var results = new List<int>();
        Assert.Equal(1, hash.Query(Vec3.Zero, 4f, results));
    }

    [Fact]
    public void Query_FindsEntriesAcrossCellBoundaries()
    {
        var hash = new SpatialHash();
        hash.Insert(1, new Vec3(3.9f, 0f, 0f)); // cell 0
        hash.Insert(2, new Vec3(4.1f, 0f, 0f)); // cell 1

        var results = new List<int>();
        Assert.Equal(2, hash.Query(new Vec3(4f, 0f, 0f), 0.5f, results));
    }

    [Fact]
    public void Query_HandlesNegativeCoordinates()
    {
        var hash = new SpatialHash();
        hash.Insert(1, new Vec3(-100.5f, -3f, -42f));

        var results = new List<int>();
        Assert.Equal(1, hash.Query(new Vec3(-100f, -3f, -42f), 1f, results));
    }

    [Fact]
    public void Query_ClearsTheResultListFirst()
    {
        var hash = new SpatialHash();
        hash.Insert(1, Vec3.Zero);

        var results = new List<int> {
            99,
            98,
        };
        hash.Query(Vec3.Zero, 1f, results);

        Assert.Equal([1], results);
    }

    [Fact]
    public void Clear_EmptiesTheIndex()
    {
        var hash = new SpatialHash();
        hash.Insert(1, Vec3.Zero);
        hash.Clear();

        var results = new List<int>();
        Assert.Equal(0, hash.Query(Vec3.Zero, 100f, results));
        Assert.Equal(0, hash.Count);
    }

    [Fact]
    public void Insert_AfterClear_Rebuilds()
    {
        var hash = new SpatialHash();
        hash.Insert(1, Vec3.Zero);
        hash.Clear();
        hash.Insert(2, new Vec3(1f, 0f, 0f));

        var results = new List<int>();
        hash.Query(Vec3.Zero, 2f, results);
        Assert.Equal([2], results);
    }

    [Fact]
    public void TryGetPosition_RoundTrips()
    {
        var hash = new SpatialHash();
        var pos = new Vec3(1f, 2f, 3f);
        hash.Insert(7, pos);

        Assert.True(hash.TryGetPosition(7, out var got));
        Assert.Equal(pos, got);
        Assert.False(hash.TryGetPosition(8, out _));
    }

    [Fact]
    public void Query_ZeroRadius_MatchesExactPosition()
    {
        var hash = new SpatialHash();
        hash.Insert(1, new Vec3(2f, 2f, 2f));

        var results = new List<int>();
        Assert.Equal(1, hash.Query(new Vec3(2f, 2f, 2f), 0f, results));
        Assert.Equal(0, hash.Query(new Vec3(2.1f, 2f, 2f), 0f, results));
    }
}
