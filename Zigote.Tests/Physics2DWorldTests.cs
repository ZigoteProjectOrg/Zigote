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

        var h = world.AddBox(Vec2.Zero, Vec2.One);
        Assert.True(h.IsValid);
        Assert.True(world.IsAlive(h));
        Assert.NotEqual(ColliderHandle.None, h);
    }

    [Fact]
    public void Remove_KillsHandle_AndQueriesStopFindingIt()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(Vec2.Zero, Vec2.One);
        var results = new List<ColliderHandle>();
        Assert.Equal(
            1,
            world.OverlapBox(
                Vec2.Zero,
                Vec2.One,
                0xFFFFFFFF,
                results
            )
        );

        world.Remove(h);
        Assert.False(world.IsAlive(h));
        Assert.Equal(
            0,
            world.OverlapBox(
                Vec2.Zero,
                Vec2.One,
                0xFFFFFFFF,
                results
            )
        );
        Assert.False(
            world.Raycast(
                new Vec2(-5f, 0f),
                Vec2.Right,
                10f,
                0xFFFFFFFF,
                out _
            )
        );
    }

    [Fact]
    public void UserDataAndLayer_RoundTrip()
    {
        var world = new CollisionWorld2D();
        var tag = new object();
        var h = world.AddBox(
            Vec2.Zero,
            Vec2.One,
            4,
            userData: tag
        );

        Assert.Same(tag, world.GetUserData(h));
        Assert.Equal(4u, world.GetLayer(h));
        world.SetLayer(h, 8);
        Assert.Equal(8u, world.GetLayer(h));
        Assert.Equal(ColliderShape2D.Box, world.GetShape(h));
        Assert.Equal(new Vec2(1f, 1f), world.GetHalfExtents(h));
    }

    // ── Overlap queries ──────────────────────────────────────────────────────

    [Fact]
    public void OverlapBox_RespectsLayerMask()
    {
        var world = new CollisionWorld2D();
        var a = world.AddBox(Vec2.Zero, Vec2.One);
        var b = world.AddBox(new Vec2(0.5f, 0f), Vec2.One, 2);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            2,
            world.OverlapBox(
                Vec2.Zero,
                Vec2.One,
                0xFFFFFFFF,
                results
            )
        );
        Assert.Equal(
            1,
            world.OverlapBox(
                Vec2.Zero,
                Vec2.One,
                1,
                results
            )
        );
        Assert.Contains(a, results);
        Assert.Equal(
            1,
            world.OverlapBox(
                Vec2.Zero,
                Vec2.One,
                2,
                results
            )
        );
        Assert.Contains(b, results);
        Assert.Equal(
            0,
            world.OverlapBox(
                Vec2.Zero,
                Vec2.One,
                4,
                results
            )
        );
    }

    [Fact]
    public void OverlapBox_TriggerFilter_AndSeparatedBoxesMiss()
    {
        var world = new CollisionWorld2D();
        world.AddBox(Vec2.Zero, Vec2.One, isTrigger: true);
        world.AddBox(new Vec2(10f, 0f), Vec2.One);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            1,
            world.OverlapBox(
                Vec2.Zero,
                Vec2.One,
                0xFFFFFFFF,
                results
            )
        );
        Assert.Equal(
            0,
            world.OverlapBox(
                Vec2.Zero,
                Vec2.One,
                0xFFFFFFFF,
                results,
                false
            )
        );
        Assert.Equal(
            0,
            world.OverlapBox(
                new Vec2(5f, 0f),
                Vec2.One,
                0xFFFFFFFF,
                results,
                false
            )
        );
    }

    [Fact]
    public void OverlapCircle_HitsBoxesAndCircles()
    {
        var world = new CollisionWorld2D();
        var box = world.AddBox(new Vec2(3f, 0f), new Vec2(1f, 1f));
        var circle = world.AddCircle(new Vec2(-3f, 0f), 1f);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            1,
            world.OverlapCircle(
                new Vec2(1.5f, 0f),
                1f,
                0xFFFFFFFF,
                results
            )
        );
        Assert.Contains(box, results);
        Assert.Equal(
            1,
            world.OverlapCircle(
                new Vec2(-1.5f, 0f),
                1f,
                0xFFFFFFFF,
                results
            )
        );
        Assert.Contains(circle, results);
        // Gap of 0.5 between circle surfaces: no hit.
        Assert.Equal(
            0,
            world.OverlapCircle(
                new Vec2(-5.5f, 0f),
                1f,
                0xFFFFFFFF,
                results
            )
        );
    }

    // ── Raycast ──────────────────────────────────────────────────────────────

    [Fact]
    public void Raycast_HitsBox_WithCorrectNormalAndDistance()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(new Vec2(5f, 0f), new Vec2(1f, 1f));

        Assert.True(
            world.Raycast(
                Vec2.Zero,
                Vec2.Right,
                20f,
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(h, hit.Collider);
        Assert.Equal(4f, hit.Distance, 4);
        Assert.Equal(-1f, hit.Normal.X, 4);
        Assert.Equal(0f, hit.Normal.Y, 4);
        Assert.Equal(4f, hit.Point.X, 4);

        // Top face from above.
        Assert.True(
            world.Raycast(
                new Vec2(5f, 5f),
                new Vec2(0f, -1f),
                20f,
                0xFFFFFFFF,
                out hit
            )
        );
        Assert.Equal(1f, hit.Normal.Y, 4);
        Assert.Equal(4f, hit.Distance, 4);
    }

    [Fact]
    public void Raycast_MissesBeyondMaxDistance_AndOffAxis()
    {
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(5f, 0f), new Vec2(1f, 1f));

        Assert.False(
            world.Raycast(
                Vec2.Zero,
                Vec2.Right,
                3f,
                0xFFFFFFFF,
                out _
            )
        );
        Assert.False(
            world.Raycast(
                Vec2.Zero,
                new Vec2(0f, 1f),
                20f,
                0xFFFFFFFF,
                out _
            )
        );
        Assert.False(
            world.Raycast(
                Vec2.Zero,
                Vec2.Right,
                20f,
                2,
                out _
            )
        ); // mask miss (collider is layer 1)
    }

    [Fact]
    public void Raycast_ReturnsClosestOfSeveral()
    {
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(10f, 0f), Vec2.One);
        var near = world.AddBox(new Vec2(5f, 0f), Vec2.One);

        Assert.True(
            world.Raycast(
                Vec2.Zero,
                Vec2.Right,
                20f,
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(near, hit.Collider);
        Assert.Equal(4f, hit.Distance, 4);
    }

    [Fact]
    public void Raycast_HitsCircle_WithRadialNormal()
    {
        var world = new CollisionWorld2D();
        var h = world.AddCircle(new Vec2(5f, 0f), 1f);

        Assert.True(
            world.Raycast(
                Vec2.Zero,
                Vec2.Right,
                20f,
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(h, hit.Collider);
        Assert.Equal(4f, hit.Distance, 4);
        Assert.Equal(-1f, hit.Normal.X, 4);

        // Diagonal graze above the circle misses.
        Assert.False(
            world.Raycast(
                new Vec2(0f, 1.5f),
                Vec2.Right,
                20f,
                0xFFFFFFFF,
                out _
            )
        );
    }

    [Fact]
    public void Raycast_NormalizesDirection()
    {
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(5f, 0f), Vec2.One);

        // Same ray, unnormalized direction: distance still in world units.
        Assert.True(
            world.Raycast(
                Vec2.Zero,
                new Vec2(100f, 0f),
                20f,
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(4f, hit.Distance, 4);
    }

    // ── SweepBox ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sweep_ReportsTimeOfImpactAndNormal()
    {
        var world = new CollisionWorld2D();
        var wall = world.AddBox(new Vec2(5f, 0f), new Vec2(0.5f, 2f));

        Assert.True(
            world.SweepBox(
                Vec2.Zero,
                new Vec2(0.5f, 0.5f),
                new Vec2(10f, 0f),
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(wall, hit.Collider);
        Assert.Equal(0.4f, hit.Time, 4); // faces meet at x = 4.5 − 0.5 = 4 → 4/10
        Assert.Equal(-1f, hit.Normal.X, 4);
        Assert.Equal(0f, hit.Normal.Y, 4);
        Assert.Equal(4.5f, hit.Point.X, 4);
    }

    [Fact]
    public void Sweep_HighSpeed_ThinWall_NoTunnelling()
    {
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(50f, 0f), new Vec2(0.025f, 2f));

        Assert.True(
            world.SweepBox(
                Vec2.Zero,
                new Vec2(0.5f, 0.5f),
                new Vec2(1000f, 0f),
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal((50f - 0.025f - 0.5f) / 1000f, hit.Time, 5);
        Assert.Equal(-1f, hit.Normal.X, 4);
    }

    [Fact]
    public void Sweep_MissesWhenPathIsClear_AndZeroDisplacementIsNoHit()
    {
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(5f, 5f), Vec2.One);

        Assert.False(
            world.SweepBox(
                Vec2.Zero,
                new Vec2(0.5f, 0.5f),
                new Vec2(10f, 0f),
                0xFFFFFFFF,
                out _
            )
        );
        Assert.False(
            world.SweepBox(
                Vec2.Zero,
                new Vec2(0.5f, 0.5f),
                Vec2.Zero,
                0xFFFFFFFF,
                out _
            )
        );
    }

    [Fact]
    public void Sweep_IgnoreHandle_Skips()
    {
        var world = new CollisionWorld2D();
        var wall = world.AddBox(new Vec2(5f, 0f), new Vec2(0.5f, 2f));

        Assert.False(
            world.SweepBox(
                Vec2.Zero,
                new Vec2(0.5f, 0.5f),
                new Vec2(10f, 0f),
                0xFFFFFFFF,
                out _,
                wall
            )
        );
    }

    [Fact]
    public void Sweep_VsCircle_FaceRegion_GivesUpNormal()
    {
        var world = new CollisionWorld2D();
        world.AddCircle(Vec2.Zero, 2f);

        // Box center within the core span (|x| ≤ 0.5): lands on the flat Minkowski face at y = 2.5.
        Assert.True(
            world.SweepBox(
                new Vec2(0.2f, 4f),
                new Vec2(0.5f, 0.5f),
                new Vec2(0f, -3f),
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal((4f - 2.5f) / 3f, hit.Time, 4);
        Assert.Equal(0f, hit.Normal.X, 4);
        Assert.Equal(1f, hit.Normal.Y, 4);
    }

    [Fact]
    public void Sweep_VsCircle_CornerRegion_GivesRadialNormal()
    {
        var world = new CollisionWorld2D();
        world.AddCircle(Vec2.Zero, 2f);

        // Box center at x = 1.5: contact via the corner circle at (0.5, 0.5) → 30°-from-vertical normal.
        Assert.True(
            world.SweepBox(
                new Vec2(1.5f, 4f),
                new Vec2(0.5f, 0.5f),
                new Vec2(0f, -3f),
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(0.5f, hit.Normal.X, 3);
        Assert.Equal(MathF.Sqrt(3f) / 2f, hit.Normal.Y, 3);
        var contactY = 0.5f + MathF.Sqrt(3f); // corner center + √(r² − dx²)
        Assert.Equal((4f - contactY) / 3f, hit.Time, 3);
        // Point sits on the circle's surface.
        Assert.Equal(2f, hit.Point.Length(), 3);
    }

    [Fact]
    public void Sweep_StartOverlapping_ReportsTimeZero_WithPushNormal()
    {
        var world = new CollisionWorld2D();
        world.AddBox(Vec2.Zero, Vec2.One);

        // Mover center just above the solid's center: minimal push is up.
        Assert.True(
            world.SweepBox(
                new Vec2(0f, 1.2f),
                new Vec2(0.5f, 0.5f),
                new Vec2(0f, -1f),
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(0f, hit.Time, 5);
        Assert.Equal(1f, hit.Normal.Y, 4);
    }

    // ── One-way platforms ────────────────────────────────────────────────────

    [Fact]
    public void OneWay_BlocksFallingFromAbove()
    {
        var world = new CollisionWorld2D();
        var plat = world.AddBox(new Vec2(0f, 2f), new Vec2(1f, 0.1f), oneWayUp: true);

        Assert.True(
            world.SweepBox(
                new Vec2(0f, 3f),
                new Vec2(0.3f, 0.3f),
                new Vec2(0f, -2f),
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(plat, hit.Collider);
        Assert.Equal((3f - 2.4f) / 2f, hit.Time, 4); // mover bottom meets platform top: 2.1 + 0.3
        Assert.Equal(1f, hit.Normal.Y, 4);
    }

    [Fact]
    public void OneWay_IgnoresRisingAndSideways()
    {
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(0f, 2f), new Vec2(1f, 0.1f), oneWayUp: true);

        // Rising from below: passes.
        Assert.False(
            world.SweepBox(
                new Vec2(0f, 1f),
                new Vec2(0.3f, 0.3f),
                new Vec2(0f, 2f),
                0xFFFFFFFF,
                out _
            )
        );
        // Pure sideways through the platform's band: passes (rule requires downward motion).
        Assert.False(
            world.SweepBox(
                new Vec2(3f, 2f),
                new Vec2(0.3f, 0.3f),
                new Vec2(-6f, 0f),
                0xFFFFFFFF,
                out _
            )
        );
        // Falling but the bottom starts below the top: passes.
        Assert.False(
            world.SweepBox(
                new Vec2(0f, 2.2f),
                new Vec2(0.3f, 0.3f),
                new Vec2(0f, -1f),
                0xFFFFFFFF,
                out _
            )
        );
    }

    [Fact]
    public void OneWay_StillReportedByOverlap()
    {
        var world = new CollisionWorld2D();
        var plat = world.AddBox(new Vec2(0f, 2f), new Vec2(1f, 0.1f), oneWayUp: true);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            1,
            world.OverlapBox(
                new Vec2(0f, 2f),
                new Vec2(0.5f, 0.5f),
                0xFFFFFFFF,
                results
            )
        );
        Assert.Contains(plat, results);
        Assert.True(world.IsOneWay(plat));
    }

    [Fact]
    public void OneWay_RaycastRule_OnlyDownwardFromAbove()
    {
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(0f, 2f), new Vec2(1f, 0.1f), oneWayUp: true);

        Assert.True(
            world.Raycast(
                new Vec2(0f, 3f),
                new Vec2(0f, -1f),
                5f,
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(3f - 2.1f, hit.Distance, 4);
        Assert.Equal(1f, hit.Normal.Y, 4);

        Assert.False(
            world.Raycast(
                new Vec2(0f, 1f),
                new Vec2(0f, 1f),
                5f,
                0xFFFFFFFF,
                out _
            )
        ); // from below
        Assert.False(
            world.Raycast(
                new Vec2(3f, 2f),
                new Vec2(-1f, 0f),
                5f,
                0xFFFFFFFF,
                out _
            )
        ); // sideways
        Assert.False(
            world.Raycast(
                new Vec2(0f, 1f),
                new Vec2(0f, -1f),
                5f,
                0xFFFFFFFF,
                out _
            )
        ); // below, downward
    }

    // ── Broadphase maintenance ───────────────────────────────────────────────

    [Fact]
    public void SetPosition_ReindexesTheGrid()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(new Vec2(100f, 100f), Vec2.One);
        // Enough far-away decoys that queries take the grid path, not the iterate-all fallback.
        for (var i = 0; i < 16; i++) world.AddBox(new Vec2(500f + i * 10f, 500f), Vec2.One);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            1,
            world.OverlapBox(
                new Vec2(100f, 100f),
                Vec2.One,
                0xFFFFFFFF,
                results
            )
        );

        world.SetPosition(h, new Vec2(-100f, -100f));
        Assert.Equal(
            0,
            world.OverlapBox(
                new Vec2(100f, 100f),
                Vec2.One,
                0xFFFFFFFF,
                results
            )
        );
        Assert.Equal(
            1,
            world.OverlapBox(
                new Vec2(-100f, -100f),
                Vec2.One,
                0xFFFFFFFF,
                results
            )
        );
        Assert.Contains(h, results);
        Assert.Equal(new Vec2(-100f, -100f), world.GetPosition(h));
    }

    [Fact]
    public void NegativeCoordinateSpace_QueriesWork()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(new Vec2(-1000f, -1000f), Vec2.One);
        for (var i = 0; i < 16; i++) world.AddBox(new Vec2(1000f + i * 10f, 1000f), Vec2.One);
        var results = new List<ColliderHandle>();

        Assert.Equal(
            1,
            world.OverlapBox(
                new Vec2(-1000f, -1000f),
                Vec2.One,
                0xFFFFFFFF,
                results
            )
        );
        Assert.Contains(h, results);

        Assert.True(
            world.Raycast(
                new Vec2(-1005f, -1000f),
                Vec2.Right,
                10f,
                0xFFFFFFFF,
                out var hit
            )
        );
        Assert.Equal(h, hit.Collider);
        Assert.Equal(4f, hit.Distance, 4);

        Assert.True(
            world.SweepBox(
                new Vec2(-1000f, -995f),
                new Vec2(0.5f, 0.5f),
                new Vec2(0f, -10f),
                0xFFFFFFFF,
                out var sweep
            )
        );
        Assert.Equal(h, sweep.Collider);
    }

    // ── Move deltas (platform carry) ─────────────────────────────────────────

    [Fact]
    public void MoveDelta_AccumulatesAcrossSetPosition_AndClearsOnBeginStep()
    {
        var world = new CollisionWorld2D();
        var h = world.AddBox(Vec2.Zero, Vec2.One);
        Assert.Equal(Vec2.Zero, world.GetMoveDelta(h));

        world.SetPosition(h, new Vec2(1f, 0f));
        world.SetPosition(h, new Vec2(1f, 2f));
        var delta = world.GetMoveDelta(h);
        Assert.Equal(1f, delta.X, 4);
        Assert.Equal(2f, delta.Y, 4);

        world.BeginStep();
        Assert.Equal(Vec2.Zero, world.GetMoveDelta(h));
        world.SetPosition(h, new Vec2(1.5f, 2f));
        Assert.Equal(0.5f, world.GetMoveDelta(h).X, 4);
    }
}
