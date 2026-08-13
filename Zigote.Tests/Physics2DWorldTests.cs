using Xunit;
using Zigote.Core.Math3D;
using Zigote.Physics2D;

namespace Zigote.Tests;

public class Physics2DWorldTests
{
    // ── Handles & lifetime ───────────────────────────────────────────────────

    [Fact]
    public void Handles_DefaultIsNone_AddReturnsValid()
    {
        var world = new CollisionWorld2D();
        Assert.False(ColliderHandle.None.IsValid);
        Assert.False(world.IsAlive(ColliderHandle.None));

        var h = world.AddBox(center: Vec2.Zero, halfExtents: Vec2.One);
        Assert.True(h.IsValid);
        Assert.True(world.IsAlive(h));
        Assert.NotEqual(expected: ColliderHandle.None, actual: h);
    }

    [Fact]
    public void Remove_KillsHandle_AndQueriesStopFindingIt()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(center: Vec2.Zero, halfExtents: Vec2.One);
        var results = new List<ColliderHandle>();
        Assert.Equal(
            expected: 1,
            actual: world.OverlapBox(
                center: Vec2.Zero,
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results
            )
        );

        world.Remove(h);
        Assert.False(world.IsAlive(h));
        Assert.Equal(
            expected: 0,
            actual: world.OverlapBox(
                center: Vec2.Zero,
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.False(
            world.Raycast(
                origin: new Vec2(x: -5f, y: 0f),
                direction: Vec2.Right,
                maxDistance: 10f,
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
    }

    [Fact]
    public void UserDataAndLayer_RoundTrip()
    {
        var world = new CollisionWorld2D();
        object tag = new();
        var h = world.AddBox(
            center: Vec2.Zero,
            halfExtents: Vec2.One,
            layer: 4,
            userData: tag
        );

        Assert.Same(expected: tag, actual: world.GetUserData(h));
        Assert.Equal(expected: 4u, actual: world.GetLayer(h));
        world.SetLayer(handle: h, layer: 8);
        Assert.Equal(expected: 8u, actual: world.GetLayer(h));
        Assert.Equal(expected: ColliderShape2D.Box, actual: world.GetShape(h));
        Assert.Equal(expected: new Vec2(x: 1f, y: 1f), actual: world.GetHalfExtents(h));
    }

    // ── Overlap queries ──────────────────────────────────────────────────────

    [Fact]
    public void OverlapBox_RespectsLayerMask()
    {
        var world = new CollisionWorld2D();
        var a = world.AddBox(center: Vec2.Zero, halfExtents: Vec2.One);
        var b = world.AddBox(center: new Vec2(x: 0.5f, y: 0f), halfExtents: Vec2.One, layer: 2);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            expected: 2,
            actual: world.OverlapBox(
                center: Vec2.Zero,
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.Equal(
            expected: 1,
            actual: world.OverlapBox(
                center: Vec2.Zero,
                halfExtents: Vec2.One,
                mask: 1,
                results: results
            )
        );
        Assert.Contains(expected: a, collection: results);
        Assert.Equal(
            expected: 1,
            actual: world.OverlapBox(
                center: Vec2.Zero,
                halfExtents: Vec2.One,
                mask: 2,
                results: results
            )
        );
        Assert.Contains(expected: b, collection: results);
        Assert.Equal(
            expected: 0,
            actual: world.OverlapBox(
                center: Vec2.Zero,
                halfExtents: Vec2.One,
                mask: 4,
                results: results
            )
        );
    }

    [Fact]
    public void OverlapBox_TriggerFilter_AndSeparatedBoxesMiss()
    {
        var world = new CollisionWorld2D();
        world.AddBox(center: Vec2.Zero, halfExtents: Vec2.One, isTrigger: true);
        world.AddBox(center: new Vec2(x: 10f, y: 0f), halfExtents: Vec2.One);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            expected: 1,
            actual: world.OverlapBox(
                center: Vec2.Zero,
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.Equal(
            expected: 0,
            actual: world.OverlapBox(
                center: Vec2.Zero,
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results,
                includeTriggers: false
            )
        );
        Assert.Equal(
            expected: 0,
            actual: world.OverlapBox(
                center: new Vec2(x: 5f, y: 0f),
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results,
                includeTriggers: false
            )
        );
    }

    [Fact]
    public void OverlapCircle_HitsBoxesAndCircles()
    {
        var world = new CollisionWorld2D();
        var box = world.AddBox(center: new Vec2(x: 3f, y: 0f), halfExtents: new Vec2(x: 1f, y: 1f));
        var circle = world.AddCircle(center: new Vec2(x: -3f, y: 0f), radius: 1f);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            expected: 1,
            actual: world.OverlapCircle(
                center: new Vec2(x: 1.5f, y: 0f),
                radius: 1f,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.Contains(expected: box, collection: results);
        Assert.Equal(
            expected: 1,
            actual: world.OverlapCircle(
                center: new Vec2(x: -1.5f, y: 0f),
                radius: 1f,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.Contains(expected: circle, collection: results);
        // Gap of 0.5 between circle surfaces: no hit.
        Assert.Equal(
            expected: 0,
            actual: world.OverlapCircle(
                center: new Vec2(x: -5.5f, y: 0f),
                radius: 1f,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
    }

    // ── Raycast ──────────────────────────────────────────────────────────────

    [Fact]
    public void Raycast_HitsBox_WithCorrectNormalAndDistance()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(center: new Vec2(x: 5f, y: 0f), halfExtents: new Vec2(x: 1f, y: 1f));

        Assert.True(
            world.Raycast(
                origin: Vec2.Zero,
                direction: Vec2.Right,
                maxDistance: 20f,
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: h, actual: hit.Collider);
        Assert.Equal(expected: 4f, actual: hit.Distance, precision: 4);
        Assert.Equal(expected: -1f, actual: hit.Normal.X, precision: 4);
        Assert.Equal(expected: 0f, actual: hit.Normal.Y, precision: 4);
        Assert.Equal(expected: 4f, actual: hit.Point.X, precision: 4);

        // Top face from above.
        Assert.True(
            world.Raycast(
                origin: new Vec2(x: 5f, y: 5f),
                direction: new Vec2(x: 0f, y: -1f),
                maxDistance: 20f,
                mask: 0xFFFFFFFF,
                hit: out hit
            )
        );
        Assert.Equal(expected: 1f, actual: hit.Normal.Y, precision: 4);
        Assert.Equal(expected: 4f, actual: hit.Distance, precision: 4);
    }

    [Fact]
    public void Raycast_MissesBeyondMaxDistance_AndOffAxis()
    {
        var world = new CollisionWorld2D();
        world.AddBox(center: new Vec2(x: 5f, y: 0f), halfExtents: new Vec2(x: 1f, y: 1f));

        Assert.False(
            world.Raycast(
                origin: Vec2.Zero,
                direction: Vec2.Right,
                maxDistance: 3f,
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
        Assert.False(
            world.Raycast(
                origin: Vec2.Zero,
                direction: new Vec2(x: 0f, y: 1f),
                maxDistance: 20f,
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
        Assert.False(
            world.Raycast(
                origin: Vec2.Zero,
                direction: Vec2.Right,
                maxDistance: 20f,
                mask: 2,
                hit: out _
            )
        ); // mask miss (collider is layer 1)
    }

    [Fact]
    public void Raycast_ReturnsClosestOfSeveral()
    {
        var world = new CollisionWorld2D();
        world.AddBox(center: new Vec2(x: 10f, y: 0f), halfExtents: Vec2.One);
        var near = world.AddBox(center: new Vec2(x: 5f, y: 0f), halfExtents: Vec2.One);

        Assert.True(
            world.Raycast(
                origin: Vec2.Zero,
                direction: Vec2.Right,
                maxDistance: 20f,
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: near, actual: hit.Collider);
        Assert.Equal(expected: 4f, actual: hit.Distance, precision: 4);
    }

    [Fact]
    public void Raycast_HitsCircle_WithRadialNormal()
    {
        var world = new CollisionWorld2D();
        var h = world.AddCircle(center: new Vec2(x: 5f, y: 0f), radius: 1f);

        Assert.True(
            world.Raycast(
                origin: Vec2.Zero,
                direction: Vec2.Right,
                maxDistance: 20f,
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: h, actual: hit.Collider);
        Assert.Equal(expected: 4f, actual: hit.Distance, precision: 4);
        Assert.Equal(expected: -1f, actual: hit.Normal.X, precision: 4);

        // Diagonal graze above the circle misses.
        Assert.False(
            world.Raycast(
                origin: new Vec2(x: 0f, y: 1.5f),
                direction: Vec2.Right,
                maxDistance: 20f,
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
    }

    [Fact]
    public void Raycast_NormalizesDirection()
    {
        var world = new CollisionWorld2D();
        world.AddBox(center: new Vec2(x: 5f, y: 0f), halfExtents: Vec2.One);

        // Same ray, unnormalized direction: distance still in world units.
        Assert.True(
            world.Raycast(
                origin: Vec2.Zero,
                direction: new Vec2(x: 100f, y: 0f),
                maxDistance: 20f,
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: 4f, actual: hit.Distance, precision: 4);
    }

    // ── SweepBox ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sweep_ReportsTimeOfImpactAndNormal()
    {
        var world = new CollisionWorld2D();
        var wall = world.AddBox(
            center: new Vec2(x: 5f, y: 0f),
            halfExtents: new Vec2(x: 0.5f, y: 2f)
        );

        Assert.True(
            world.SweepBox(
                center: Vec2.Zero,
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: new Vec2(x: 10f, y: 0f),
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: wall, actual: hit.Collider);
        Assert.Equal(
            expected: 0.4f,
            actual: hit.Time,
            precision: 4
        ); // faces meet at x = 4.5 − 0.5 = 4 → 4/10
        Assert.Equal(expected: -1f, actual: hit.Normal.X, precision: 4);
        Assert.Equal(expected: 0f, actual: hit.Normal.Y, precision: 4);
        Assert.Equal(expected: 4.5f, actual: hit.Point.X, precision: 4);
    }

    [Fact]
    public void Sweep_HighSpeed_ThinWall_NoTunnelling()
    {
        var world = new CollisionWorld2D();
        world.AddBox(center: new Vec2(x: 50f, y: 0f), halfExtents: new Vec2(x: 0.025f, y: 2f));

        Assert.True(
            world.SweepBox(
                center: Vec2.Zero,
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: new Vec2(x: 1000f, y: 0f),
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: (50f - 0.025f - 0.5f) / 1000f, actual: hit.Time, precision: 5);
        Assert.Equal(expected: -1f, actual: hit.Normal.X, precision: 4);
    }

    [Fact]
    public void Sweep_MissesWhenPathIsClear_AndZeroDisplacementIsNoHit()
    {
        var world = new CollisionWorld2D();
        world.AddBox(center: new Vec2(x: 5f, y: 5f), halfExtents: Vec2.One);

        Assert.False(
            world.SweepBox(
                center: Vec2.Zero,
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: new Vec2(x: 10f, y: 0f),
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
        Assert.False(
            world.SweepBox(
                center: Vec2.Zero,
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: Vec2.Zero,
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
    }

    [Fact]
    public void Sweep_IgnoreHandle_Skips()
    {
        var world = new CollisionWorld2D();
        var wall = world.AddBox(
            center: new Vec2(x: 5f, y: 0f),
            halfExtents: new Vec2(x: 0.5f, y: 2f)
        );

        Assert.False(
            world.SweepBox(
                center: Vec2.Zero,
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: new Vec2(x: 10f, y: 0f),
                mask: 0xFFFFFFFF,
                hit: out _,
                ignore: wall
            )
        );
    }

    [Fact]
    public void Sweep_VsCircle_FaceRegion_GivesUpNormal()
    {
        var world = new CollisionWorld2D();
        world.AddCircle(center: Vec2.Zero, radius: 2f);

        // Box center within the core span (|x| ≤ 0.5): lands on the flat Minkowski face at y = 2.5.
        Assert.True(
            world.SweepBox(
                center: new Vec2(x: 0.2f, y: 4f),
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: new Vec2(x: 0f, y: -3f),
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: (4f - 2.5f) / 3f, actual: hit.Time, precision: 4);
        Assert.Equal(expected: 0f, actual: hit.Normal.X, precision: 4);
        Assert.Equal(expected: 1f, actual: hit.Normal.Y, precision: 4);
    }

    [Fact]
    public void Sweep_VsCircle_CornerRegion_GivesRadialNormal()
    {
        var world = new CollisionWorld2D();
        world.AddCircle(center: Vec2.Zero, radius: 2f);

        // Box center at x = 1.5: contact via the corner circle at (0.5, 0.5) → 30°-from-vertical normal.
        Assert.True(
            world.SweepBox(
                center: new Vec2(x: 1.5f, y: 4f),
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: new Vec2(x: 0f, y: -3f),
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: 0.5f, actual: hit.Normal.X, precision: 3);
        Assert.Equal(expected: MathF.Sqrt(3f) / 2f, actual: hit.Normal.Y, precision: 3);
        float contactY = 0.5f + MathF.Sqrt(3f); // corner center + √(r² − dx²)
        Assert.Equal(expected: (4f - contactY) / 3f, actual: hit.Time, precision: 3);
        // Point sits on the circle's surface.
        Assert.Equal(expected: 2f, actual: hit.Point.Length(), precision: 3);
    }

    [Fact]
    public void Sweep_StartOverlapping_ReportsTimeZero_WithPushNormal()
    {
        var world = new CollisionWorld2D();
        world.AddBox(center: Vec2.Zero, halfExtents: Vec2.One);

        // Mover center just above the solid's center: minimal push is up.
        Assert.True(
            world.SweepBox(
                center: new Vec2(x: 0f, y: 1.2f),
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: new Vec2(x: 0f, y: -1f),
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: 0f, actual: hit.Time, precision: 5);
        Assert.Equal(expected: 1f, actual: hit.Normal.Y, precision: 4);
    }

    // ── One-way platforms ────────────────────────────────────────────────────

    [Fact]
    public void OneWay_BlocksFallingFromAbove()
    {
        var world = new CollisionWorld2D();
        var plat = world.AddBox(
            center: new Vec2(x: 0f, y: 2f),
            halfExtents: new Vec2(x: 1f, y: 0.1f),
            oneWayUp: true
        );

        Assert.True(
            world.SweepBox(
                center: new Vec2(x: 0f, y: 3f),
                halfExtents: new Vec2(x: 0.3f, y: 0.3f),
                displacement: new Vec2(x: 0f, y: -2f),
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: plat, actual: hit.Collider);
        Assert.Equal(
            expected: (3f - 2.4f) / 2f,
            actual: hit.Time,
            precision: 4
        ); // mover bottom meets platform top: 2.1 + 0.3
        Assert.Equal(expected: 1f, actual: hit.Normal.Y, precision: 4);
    }

    [Fact]
    public void OneWay_IgnoresRisingAndSideways()
    {
        var world = new CollisionWorld2D();
        world.AddBox(
            center: new Vec2(x: 0f, y: 2f),
            halfExtents: new Vec2(x: 1f, y: 0.1f),
            oneWayUp: true
        );

        // Rising from below: passes.
        Assert.False(
            world.SweepBox(
                center: new Vec2(x: 0f, y: 1f),
                halfExtents: new Vec2(x: 0.3f, y: 0.3f),
                displacement: new Vec2(x: 0f, y: 2f),
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
        // Pure sideways through the platform's band: passes (rule requires downward motion).
        Assert.False(
            world.SweepBox(
                center: new Vec2(x: 3f, y: 2f),
                halfExtents: new Vec2(x: 0.3f, y: 0.3f),
                displacement: new Vec2(x: -6f, y: 0f),
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
        // Falling but the bottom starts below the top: passes.
        Assert.False(
            world.SweepBox(
                center: new Vec2(x: 0f, y: 2.2f),
                halfExtents: new Vec2(x: 0.3f, y: 0.3f),
                displacement: new Vec2(x: 0f, y: -1f),
                mask: 0xFFFFFFFF,
                hit: out _
            )
        );
    }

    [Fact]
    public void OneWay_StillReportedByOverlap()
    {
        var world = new CollisionWorld2D();
        var plat = world.AddBox(
            center: new Vec2(x: 0f, y: 2f),
            halfExtents: new Vec2(x: 1f, y: 0.1f),
            oneWayUp: true
        );
        var results = new List<ColliderHandle>();

        Assert.Equal(
            expected: 1,
            actual: world.OverlapBox(
                center: new Vec2(x: 0f, y: 2f),
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.Contains(expected: plat, collection: results);
        Assert.True(world.IsOneWay(plat));
    }

    [Fact]
    public void OneWay_RaycastRule_OnlyDownwardFromAbove()
    {
        var world = new CollisionWorld2D();
        world.AddBox(
            center: new Vec2(x: 0f, y: 2f),
            halfExtents: new Vec2(x: 1f, y: 0.1f),
            oneWayUp: true
        );

        Assert.True(
            world.Raycast(
                origin: new Vec2(x: 0f, y: 3f),
                direction: new Vec2(x: 0f, y: -1f),
                maxDistance: 5f,
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: 3f - 2.1f, actual: hit.Distance, precision: 4);
        Assert.Equal(expected: 1f, actual: hit.Normal.Y, precision: 4);

        Assert.False(
            world.Raycast(
                origin: new Vec2(x: 0f, y: 1f),
                direction: new Vec2(x: 0f, y: 1f),
                maxDistance: 5f,
                mask: 0xFFFFFFFF,
                hit: out _
            )
        ); // from below
        Assert.False(
            world.Raycast(
                origin: new Vec2(x: 3f, y: 2f),
                direction: new Vec2(x: -1f, y: 0f),
                maxDistance: 5f,
                mask: 0xFFFFFFFF,
                hit: out _
            )
        ); // sideways
        Assert.False(
            world.Raycast(
                origin: new Vec2(x: 0f, y: 1f),
                direction: new Vec2(x: 0f, y: -1f),
                maxDistance: 5f,
                mask: 0xFFFFFFFF,
                hit: out _
            )
        ); // below, downward
    }

    // ── Broadphase maintenance ───────────────────────────────────────────────

    [Fact]
    public void SetPosition_ReindexesTheGrid()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(center: new Vec2(x: 100f, y: 100f), halfExtents: Vec2.One);
        // Enough far-away decoys that queries take the grid path, not the iterate-all fallback.
        for (int i = 0; i < 16; i++)
            world.AddBox(center: new Vec2(x: 500f + (i * 10f), y: 500f), halfExtents: Vec2.One);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            expected: 1,
            actual: world.OverlapBox(
                center: new Vec2(x: 100f, y: 100f),
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results
            )
        );

        world.SetPosition(handle: h, position: new Vec2(x: -100f, y: -100f));
        Assert.Equal(
            expected: 0,
            actual: world.OverlapBox(
                center: new Vec2(x: 100f, y: 100f),
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.Equal(
            expected: 1,
            actual: world.OverlapBox(
                center: new Vec2(x: -100f, y: -100f),
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.Contains(expected: h, collection: results);
        Assert.Equal(expected: new Vec2(x: -100f, y: -100f), actual: world.GetPosition(h));
    }

    [Fact]
    public void NegativeCoordinateSpace_QueriesWork()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(center: new Vec2(x: -1000f, y: -1000f), halfExtents: Vec2.One);
        for (int i = 0; i < 16; i++)
            world.AddBox(center: new Vec2(x: 1000f + (i * 10f), y: 1000f), halfExtents: Vec2.One);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            expected: 1,
            actual: world.OverlapBox(
                center: new Vec2(x: -1000f, y: -1000f),
                halfExtents: Vec2.One,
                mask: 0xFFFFFFFF,
                results: results
            )
        );
        Assert.Contains(expected: h, collection: results);

        Assert.True(
            world.Raycast(
                origin: new Vec2(x: -1005f, y: -1000f),
                direction: Vec2.Right,
                maxDistance: 10f,
                mask: 0xFFFFFFFF,
                hit: out var hit
            )
        );
        Assert.Equal(expected: h, actual: hit.Collider);
        Assert.Equal(expected: 4f, actual: hit.Distance, precision: 4);

        Assert.True(
            world.SweepBox(
                center: new Vec2(x: -1000f, y: -995f),
                halfExtents: new Vec2(x: 0.5f, y: 0.5f),
                displacement: new Vec2(x: 0f, y: -10f),
                mask: 0xFFFFFFFF,
                hit: out var sweep
            )
        );
        Assert.Equal(expected: h, actual: sweep.Collider);
    }

    // ── Move deltas (platform carry) ─────────────────────────────────────────

    [Fact]
    public void MoveDelta_AccumulatesAcrossSetPosition_AndClearsOnBeginStep()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(center: Vec2.Zero, halfExtents: Vec2.One);
        Assert.Equal(expected: Vec2.Zero, actual: world.GetMoveDelta(h));

        world.SetPosition(handle: h, position: new Vec2(x: 1f, y: 0f));
        world.SetPosition(handle: h, position: new Vec2(x: 1f, y: 2f));
        var delta = world.GetMoveDelta(h);
        Assert.Equal(expected: 1f, actual: delta.X, precision: 4);
        Assert.Equal(expected: 2f, actual: delta.Y, precision: 4);

        world.BeginStep();
        Assert.Equal(expected: Vec2.Zero, actual: world.GetMoveDelta(h));
        world.SetPosition(handle: h, position: new Vec2(x: 1.5f, y: 2f));
        Assert.Equal(expected: 0.5f, actual: world.GetMoveDelta(h).X, precision: 4);
    }
}
