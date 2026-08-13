using Xunit;
using Zigote.Core.Math3D;
using Zigote.Physics2D;

namespace Zigote.Tests;

public class CharacterController2DTests
{
    private const float Dt = 1f / 120f;
    private const float Gravity = 30f;
    private static readonly Vec2 CharacterHalf = new(0.4f, 0.5f);

    // ── Resting & landing ────────────────────────────────────────────────────

    [Fact]
    public void RestsOnGround_NoSinkNoJitter_Over300Ticks()
    {
        var world = FlatGround(out var ground);
        var c = Spawn(world, new Vec2(0f, 0.6f));
        Settle(c);
        Assert.True(c.IsGrounded);
        Assert.Equal(ground, c.GroundCollider);

        var restX = c.Position.X;
        var restY = c.Position.Y;
        for (var i = 0; i < 300; i++)
        {
            Tick(c);
            Assert.True(c.IsGrounded, $"lost ground on tick {i}");
            Assert.True(
                MathF.Abs(c.Position.X - restX) < 1e-3f,
                $"X drifted to {c.Position.X} on tick {i}"
            );
            Assert.True(
                MathF.Abs(c.Position.Y - restY) < 1e-3f,
                $"Y jittered to {c.Position.Y} on tick {i}"
            );
        }

        var bottom = c.Position.Y - CharacterHalf.Y;
        Assert.InRange(bottom, -1e-4f, c.SkinWidth + 1e-3f);
    }

    [Fact]
    public void FallsUnderGravity_AndLandsWithinSkin()
    {
        var world = FlatGround(out var ground);
        var c = Spawn(world, new Vec2(0f, 5.5f));

        var landedAt = -1;
        for (var i = 0; i < 400 && landedAt < 0; i++)
        {
            Tick(c);
            if (c.IsGrounded) landedAt = i;
        }

        Assert.True(landedAt > 0, "never landed");
        Assert.Equal(ground, c.GroundCollider);
        Assert.Equal(1f, c.GroundNormal.Y, 3);
        Assert.InRange(c.Position.Y - CharacterHalf.Y, -1e-4f, c.SkinWidth + 1e-3f);
        Assert.Equal(0f, c.Velocity.Y, 3); // impact velocity absorbed by the surface
    }

    // ── Walking ──────────────────────────────────────────────────────────────

    [Fact]
    public void WalksFlatGround_AtConstantSpeed()
    {
        var world = FlatGround(out _);
        var c = Spawn(world, new Vec2(0f, 0.6f));
        Settle(c);

        for (var i = 0; i < 120; i++)
        {
            var prevX = c.Position.X;
            Tick(c, 3f);
            Assert.True(c.IsGrounded, $"lost ground on tick {i}");
            Assert.Equal(3f * Dt, c.Position.X - prevX, 4);
        }
    }

    [Fact]
    public void WalksAcrossThreeAdjacentTiles_WithoutSnagging()
    {
        // Three edge-sharing tiles, tops flush at y = 0, spanning x ∈ [−3, 3].
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(-2f, -0.5f), new Vec2(1f, 0.5f));
        world.AddBox(new Vec2(0f, -0.5f), new Vec2(1f, 0.5f));
        world.AddBox(new Vec2(2f, -0.5f), new Vec2(1f, 0.5f));
        var c = Spawn(world, new Vec2(-2.5f, 0.6f));
        Settle(c);
        Assert.True(c.IsGrounded);

        for (var i = 0; i < 150; i++)
        {
            var prevX = c.Position.X;
            Tick(c, 4f);
            Assert.True(c.IsGrounded, $"lost ground on tick {i} at x={c.Position.X}");
            Assert.False(c.IsOnWall, $"ghost wall on tick {i} at x={c.Position.X}");
            Assert.True(
                c.Position.X - prevX >= 4f * Dt - 1e-4f,
                $"snagged on tick {i}: moved {c.Position.X - prevX} at x={c.Position.X}"
            );
        }

        Assert.Equal(2.5f, c.Position.X, 2);
    }

    // ── Walls & ceilings ─────────────────────────────────────────────────────

    [Fact]
    public void SlidesAlongWall_XStops_YContinues()
    {
        var world = new CollisionWorld2D();
        world.AddBox(new Vec2(3.9f, 0f), new Vec2(0.5f, 5f)); // left face at x = 3.4
        var c = Spawn(world, new Vec2(2f, 3f));

        var contact = -1;
        for (var i = 0; i < 60; i++)
        {
            var prevY = c.Position.Y;
            c.Velocity = new Vec2(5f, c.Velocity.Y - Gravity * Dt); // hold "right" while falling
            c.Move(Dt);
            if (contact < 0 && c.IsOnWall) contact = i;
            if (contact < 0) continue;

            Assert.True(c.IsOnWall, $"wall contact lost on tick {i}");
            Assert.False(c.IsGrounded);
            Assert.Equal(3f - c.SkinWidth, c.Position.X, 3); // pinned a skin off the face
            Assert.Equal(0f, c.Velocity.X, 4);
            Assert.True(c.Position.Y < prevY, $"Y stopped falling on tick {i}");
        }

        Assert.True(contact >= 0, "never touched the wall");
    }

    [Fact]
    public void Ceiling_StopsUpwardMotion_AndSetsFlag()
    {
        var world = FlatGround(out _);
        world.AddBox(new Vec2(0f, 3.5f), new Vec2(2f, 0.5f)); // underside at y = 3
        var c = Spawn(world, new Vec2(0f, 0.6f));
        Settle(c);

        c.Velocity = new Vec2(0f, 12f);
        var bonked = -1;
        for (var i = 0; i < 120 && bonked < 0; i++)
        {
            Tick(c);
            if (c.IsOnCeiling) bonked = i;
        }

        Assert.True(bonked >= 0, "never reached the ceiling");
        Assert.True(c.Velocity.Y <= 0f, "upward velocity survived the ceiling");
        Assert.True(
            c.Position.Y + CharacterHalf.Y <= 3f + 1e-3f,
            $"head inside ceiling at {c.Position.Y}"
        );

        for (var i = 0; i < 200 && !c.IsGrounded; i++) Tick(c);
        Assert.True(c.IsGrounded, "did not fall back to the ground");
    }

    // ── Slopes (large circle = curved slope; normal varies with position) ────

    [Fact]
    public void Ascends30DegreeSlope_GroundedThroughout()
    {
        var world = SlopeCircle();
        var c = Spawn(world, new Vec2(10.4f, -1.9f)); // ~30° flank, box corner contact
        Settle(c);
        Assert.True(c.IsGrounded);
        Assert.True(c.GroundNormal.Y < 0.95f, "expected a sloped ground normal");

        var startX = c.Position.X;
        var startY = c.Position.Y;
        for (var i = 0; i < 90; i++)
        {
            Tick(c, -2f); // toward the crest
            Assert.True(c.IsGrounded, $"lost ground on tick {i} at x={c.Position.X}");
        }

        Assert.True(c.Position.X < startX - 1f, "made no uphill progress");
        Assert.True(c.Position.Y > startY + 0.3f, "did not climb");
    }

    [Fact]
    public void Descends30DegreeSlope_SnapKeepsItGlued()
    {
        var world = SlopeCircle();
        var c = Spawn(world, new Vec2(3.872f, 0.5f)); // ~10° near the crest
        Settle(c);
        Assert.True(c.IsGrounded);

        var startX = c.Position.X;
        var startY = c.Position.Y;
        for (var i = 0; i < 100; i++)
        {
            Tick(c, 3f); // away from the crest — surface falls away underfoot
            Assert.True(c.IsGrounded, $"came unglued on tick {i} at x={c.Position.X}");
        }

        Assert.True(c.Position.X > startX + 1.5f, "made no downhill progress");
        Assert.True(c.Position.Y < startY - 0.3f, "did not descend");
    }

    [Fact]
    public void SteepSlope70Degrees_DoesNotGround_Slides()
    {
        var world = SlopeCircle();
        // ~70° flank: normal.Y ≈ cos70° ≈ 0.342 < cos50° — unwalkable.
        var c = Spawn(world, new Vec2(19.194f, -12.4f));

        var touchedWall = false;
        var startX = c.Position.X;
        for (var i = 0; i < 50; i++)
        {
            Tick(c);
            Assert.False(c.IsGrounded, $"grounded on a 70° surface on tick {i}");
            touchedWall |= c.IsOnWall;
        }

        Assert.True(touchedWall, "never registered the steep surface as a wall");
        Assert.True(c.Position.X > startX, "did not slide down the flank");
    }

    // ── Jumping & snap interaction ───────────────────────────────────────────

    [Fact]
    public void Jump_IsNotSnappedBackToGround()
    {
        var world = FlatGround(out _);
        var c = Spawn(world, new Vec2(0f, 0.6f));
        Settle(c);
        var groundY = c.Position.Y;

        c.Velocity = new Vec2(0f, 8f);
        c.Move(Dt);
        Assert.False(c.IsGrounded, "jump was snapped back");
        Assert.Equal(groundY + 8f * Dt, c.Position.Y, 3);

        for (var i = 0; i < 10; i++)
        {
            var prevY = c.Position.Y;
            Tick(c);
            Assert.True(c.Position.Y > prevY, $"stopped rising on tick {i}");
            Assert.False(c.IsGrounded);
        }
    }

    [Fact]
    public void TimeSinceGrounded_AccumulatesInAir_ResetsOnLanding()
    {
        var world = FlatGround(out _);
        var c = Spawn(world, new Vec2(0f, 0.6f));
        Settle(c);
        Assert.Equal(0f, c.TimeSinceGrounded);

        c.Velocity = new Vec2(0f, 6f);
        c.Move(Dt);
        for (var i = 0; i < 29; i++) Tick(c);
        Assert.False(c.IsGrounded);
        Assert.Equal(30f * Dt, c.TimeSinceGrounded, 3); // the coyote-time building block

        for (var i = 0; i < 200 && !c.IsGrounded; i++) Tick(c);
        Assert.True(c.IsGrounded);
        Assert.Equal(0f, c.TimeSinceGrounded);
    }

    // ── One-way platforms ────────────────────────────────────────────────────

    [Fact]
    public void OneWay_LandsWhenFallingFromAbove()
    {
        var world = FlatGround(out _);
        var plat = world.AddBox(new Vec2(0f, 2f), new Vec2(2f, 0.1f), oneWayUp: true); // top at 2.1
        var c = Spawn(world, new Vec2(0f, 3.5f));
        Settle(c, 200);

        Assert.True(c.IsGrounded);
        Assert.Equal(plat, c.GroundCollider);
        Assert.Equal(2.1f + c.SkinWidth, c.Position.Y - CharacterHalf.Y, 2);
    }

    [Fact]
    public void OneWay_JumpUpThroughIt_Unimpeded_ThenLandsOnIt()
    {
        var world = FlatGround(out var ground);
        var plat = world.AddBox(new Vec2(0f, 2f), new Vec2(2f, 0.1f), oneWayUp: true);
        var c = Spawn(world, new Vec2(0f, 0.6f));
        Settle(c);
        Assert.Equal(ground, c.GroundCollider);

        c.Velocity = new Vec2(0f, 14f);
        c.Move(Dt);
        var rosePastPlatform = false;
        for (var i = 0; i < 400 && !c.IsGrounded; i++)
        {
            Tick(c);
            Assert.False(c.IsOnCeiling, $"one-way blocked the ascent on tick {i}");
            if (c.Position.Y - CharacterHalf.Y > 2.2f) rosePastPlatform = true;
        }

        Assert.True(rosePastPlatform, "jump never cleared the platform");
        Assert.True(c.IsGrounded);
        Assert.Equal(plat, c.GroundCollider); // came back down onto the platform's top
    }

    [Fact]
    public void OneWay_DropThrough_FallsThrough_AndIgnoreAutoClears()
    {
        var world = FlatGround(out var ground);
        var plat = world.AddBox(new Vec2(0f, 2f), new Vec2(2f, 0.1f), oneWayUp: true);
        var c = Spawn(world, new Vec2(0f, 3.5f));
        Settle(c, 200);
        Assert.Equal(plat, c.GroundCollider);

        c.DropThrough();
        for (var i = 0; i < 300 && c.GroundCollider != ground; i++) Tick(c);
        Assert.True(c.IsGrounded);
        Assert.Equal(ground, c.GroundCollider); // fell through onto the real floor
        Assert.InRange(c.Position.Y - CharacterHalf.Y, -1e-4f, c.SkinWidth + 1e-3f);

        // The ignore must have cleared once fully past: a fresh jump lands back ON the platform.
        c.Velocity = new Vec2(0f, 14f);
        c.Move(Dt);
        for (var i = 0; i < 400 && !c.IsGrounded; i++) Tick(c);
        Assert.Equal(plat, c.GroundCollider);
    }

    // ── Moving platforms ─────────────────────────────────────────────────────

    [Fact]
    public void MovingPlatform_CarriesHorizontally()
    {
        var world = new CollisionWorld2D();
        var plat = world.AddBox(new Vec2(0f, 1f), new Vec2(1.5f, 0.25f)); // top at 1.25
        var c = Spawn(world, new Vec2(0f, 1.8f));
        Settle(c);
        Assert.Equal(plat, c.GroundCollider);

        var px = 0f;
        for (var i = 0; i < 120; i++)
        {
            world.BeginStep();
            px += 2f * Dt;
            world.SetPosition(plat, new Vec2(px, 1f));
            Tick(c);
            Assert.True(c.IsGrounded, $"fell off the conveyor on tick {i}");
        }

        Assert.Equal(px, c.Position.X, 2); // rode the full 2 units
    }

    [Fact]
    public void MovingPlatform_ElevatorCarriesVertically_Over200Ticks()
    {
        var world = new CollisionWorld2D();
        var plat = world.AddBox(new Vec2(0f, 1f), new Vec2(1.5f, 0.25f));
        var c = Spawn(world, new Vec2(0f, 1.8f));
        Settle(c);
        Assert.Equal(plat, c.GroundCollider);

        var py = 1f;
        for (var i = 0; i < 200; i++)
        {
            world.BeginStep();
            py += 1.5f * Dt; // ascending elevator
            world.SetPosition(plat, new Vec2(0f, py));
            Tick(c);
            Assert.True(c.IsGrounded, $"lost the elevator on tick {i}");
            var gap = c.Position.Y - CharacterHalf.Y - (py + 0.25f);
            Assert.InRange(gap, -0.005f, 0.03f);
        }

        for (var i = 0; i < 100; i++)
        {
            world.BeginStep();
            py -= 1.5f * Dt; // and back down
            world.SetPosition(plat, new Vec2(0f, py));
            Tick(c);
            Assert.True(c.IsGrounded, $"lost the descending elevator on tick {i}");
        }

        Assert.Equal(py + 0.25f + c.SkinWidth, c.Position.Y - CharacterHalf.Y, 2);
    }

    // ── Triggers & masks ─────────────────────────────────────────────────────

    [Fact]
    public void Triggers_EnterAndExit_FireExactlyOnce()
    {
        var world = FlatGround(out _);
        var zone = world.AddBox(new Vec2(2.5f, 0.5f), new Vec2(0.5f, 1f), isTrigger: true);
        var c = Spawn(world, new Vec2(0f, 0.6f));
        Settle(c);

        int enters = 0, exits = 0;
        for (var i = 0; i < 150; i++)
        {
            Tick(c, 4f);
            if (c.TriggersEntered.Count > 0)
            {
                enters += c.TriggersEntered.Count;
                Assert.Equal(zone, c.TriggersEntered[0]);
            }

            if (c.TriggersExited.Count > 0)
            {
                exits += c.TriggersExited.Count;
                Assert.Equal(zone, c.TriggersExited[0]);
            }
        }

        Assert.True(c.Position.X > 4f); // walked all the way through
        Assert.Equal(1, enters);
        Assert.Equal(1, exits);
    }

    [Fact]
    public void LayerMask_PassesThroughCollidersOutsideTheMask()
    {
        var world = FlatGround(out _); // ground is layer 1
        world.AddBox(new Vec2(2f, 1.5f), new Vec2(0.2f, 2f), 2); // a wall the mask excludes
        var c = Spawn(world, new Vec2(0f, 0.6f));
        c.CollisionMask = 1;
        Settle(c);

        for (var i = 0; i < 90; i++)
        {
            Tick(c, 4f);
            Assert.True(c.IsGrounded);
            Assert.False(c.IsOnWall);
        }

        Assert.True(
            c.Position.X > 2.4f,
            $"was blocked at x={c.Position.X}"
        ); // walked straight through
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CollisionWorld2D FlatGround(out ColliderHandle ground)
    {
        var world = new CollisionWorld2D();
        ground = world.AddBox(new Vec2(0f, -0.5f), new Vec2(50f, 0.5f)); // top at y = 0
        return world;
    }

    /// <summary>
    ///     R=20 circle whose top touches the origin — a curved hill. On the right flank the surface
    ///     normal tilts with x, so one shape provides 10°/30°/70° "slopes" for the walkability tests.
    /// </summary>
    private static CollisionWorld2D SlopeCircle()
    {
        var world = new CollisionWorld2D();
        world.AddCircle(new Vec2(0f, -20f), 20f);
        return world;
    }

    private static CharacterController2D Spawn(CollisionWorld2D world, Vec2 position)
    {
        return new CharacterController2D(world, CharacterHalf) { Position = position };
    }

    /// <summary>One fixed game tick: the game integrates gravity into Velocity, then the controller moves.</summary>
    private static void Tick(CharacterController2D c, float vx = 0f)
    {
        c.Velocity = new Vec2(vx, c.Velocity.Y - Gravity * Dt);
        c.Move(Dt);
    }

    private static void Settle(CharacterController2D c, int ticks = 60)
    {
        for (var i = 0; i < ticks; i++) Tick(c);
    }
}
