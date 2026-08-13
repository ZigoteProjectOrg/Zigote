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
        hash.Insert(id: 1, position: new Vec3(x: 0f, y: 0f, z: 0f));
        hash.Insert(id: 2, position: new Vec3(x: 3f, y: 0f, z: 0f));
        hash.Insert(id: 3, position: new Vec3(x: 0f, y: 10f, z: 0f));

        var results = new List<int>();
        int count = hash.Query(center: Vec3.Zero, radius: 5f, results: results);

        Assert.Equal(expected: 2, actual: count);
        Assert.Contains(expected: 1, collection: results);
        Assert.Contains(expected: 2, collection: results);
        Assert.DoesNotContain(expected: 3, collection: results);
    }

    [Fact]
    public void Query_RadiusIsInclusive()
    {
        var hash = new SpatialHash();
        hash.Insert(id: 1, position: new Vec3(x: 4f, y: 0f, z: 0f));

        var results = new List<int>();
        Assert.Equal(
            expected: 1,
            actual: hash.Query(center: Vec3.Zero, radius: 4f, results: results)
        );
    }

    [Fact]
    public void Query_FindsEntriesAcrossCellBoundaries()
    {
        var hash = new SpatialHash();
        hash.Insert(id: 1, position: new Vec3(x: 3.9f, y: 0f, z: 0f)); // cell 0
        hash.Insert(id: 2, position: new Vec3(x: 4.1f, y: 0f, z: 0f)); // cell 1

        var results = new List<int>();
        Assert.Equal(
            expected: 2,
            actual: hash.Query(
                center: new Vec3(x: 4f, y: 0f, z: 0f),
                radius: 0.5f,
                results: results
            )
        );
    }

    [Fact]
    public void Query_HandlesNegativeCoordinates()
    {
        var hash = new SpatialHash();
        hash.Insert(id: 1, position: new Vec3(x: -100.5f, y: -3f, z: -42f));

        var results = new List<int>();
        Assert.Equal(
            expected: 1,
            actual: hash.Query(
                center: new Vec3(x: -100f, y: -3f, z: -42f),
                radius: 1f,
                results: results
            )
        );
    }

    [Fact]
    public void Query_ClearsTheResultListFirst()
    {
        var hash = new SpatialHash();
        hash.Insert(id: 1, position: Vec3.Zero);

        var results = new List<int> {
            99,
            98,
        };
        hash.Query(center: Vec3.Zero, radius: 1f, results: results);

        Assert.Equal(expected: [1], actual: results);
    }

    [Fact]
    public void Clear_EmptiesTheIndex()
    {
        var hash = new SpatialHash();
        hash.Insert(id: 1, position: Vec3.Zero);
        hash.Clear();

        var results = new List<int>();
        Assert.Equal(
            expected: 0,
            actual: hash.Query(center: Vec3.Zero, radius: 100f, results: results)
        );
        Assert.Equal(expected: 0, actual: hash.Count);
    }

    [Fact]
    public void Insert_AfterClear_Rebuilds()
    {
        var hash = new SpatialHash();
        hash.Insert(id: 1, position: Vec3.Zero);
        hash.Clear();
        hash.Insert(id: 2, position: new Vec3(x: 1f, y: 0f, z: 0f));

        var results = new List<int>();
        hash.Query(center: Vec3.Zero, radius: 2f, results: results);
        Assert.Equal(expected: [2], actual: results);
    }

    [Fact]
    public void TryGetPosition_RoundTrips()
    {
        var hash = new SpatialHash();
        var pos = new Vec3(x: 1f, y: 2f, z: 3f);
        hash.Insert(id: 7, position: pos);

        Assert.True(hash.TryGetPosition(id: 7, position: out var got));
        Assert.Equal(expected: pos, actual: got);
        Assert.False(hash.TryGetPosition(id: 8, position: out _));
    }

    [Fact]
    public void Query_ZeroRadius_MatchesExactPosition()
    {
        var hash = new SpatialHash();
        hash.Insert(id: 1, position: new Vec3(x: 2f, y: 2f, z: 2f));

        var results = new List<int>();
        Assert.Equal(
            expected: 1,
            actual: hash.Query(center: new Vec3(x: 2f, y: 2f, z: 2f), radius: 0f, results: results)
        );
        Assert.Equal(
            expected: 0,
            actual: hash.Query(
                center: new Vec3(x: 2.1f, y: 2f, z: 2f),
                radius: 0f,
                results: results
            )
        );
    }
}
