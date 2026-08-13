using Xunit;
using Zigote.Core.Math3D;
using Zigote.Physics2D;

namespace Zigote.Tests;

public class CharacterController2DTests
{
    private const float Dt = 1f / 120f;
    private const float Gravity = 30f;
    private static readonly Vec2 CharacterHalf = new(x: 0.4f, y: 0.5f);

    // ── Resting & landing ────────────────────────────────────────────────────

    [Fact]
    public void RestsOnGround_NoSinkNoJitter_Over300Ticks()
    {
        var world = FlatGround(out var ground);
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 0.6f));
        Settle(c);
        Assert.True(c.IsGrounded);
        Assert.Equal(expected: ground, actual: c.GroundCollider);

        float restX = c.Position.X;
        float restY = c.Position.Y;
        for (int i = 0; i < 300; i++)
        {
            Tick(c);
            Assert.True(condition: c.IsGrounded, userMessage: $"lost ground on tick {i}");
            Assert.True(
                condition: MathF.Abs(c.Position.X - restX) < 1e-3f,
                userMessage: $"X drifted to {c.Position.X} on tick {i}"
            );
            Assert.True(
                condition: MathF.Abs(c.Position.Y - restY) < 1e-3f,
                userMessage: $"Y jittered to {c.Position.Y} on tick {i}"
            );
        }

        float bottom = c.Position.Y - CharacterHalf.Y;
        Assert.InRange(actual: bottom, low: -1e-4f, high: c.SkinWidth + 1e-3f);
    }

    [Fact]
    public void FallsUnderGravity_AndLandsWithinSkin()
    {
        var world = FlatGround(out var ground);
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 5.5f));

        int landedAt = -1;
        for (int i = 0; i < 400 && landedAt < 0; i++)
        {
            Tick(c);
            if (c.IsGrounded) landedAt = i;
        }

        Assert.True(condition: landedAt > 0, userMessage: "never landed");
        Assert.Equal(expected: ground, actual: c.GroundCollider);
        Assert.Equal(expected: 1f, actual: c.GroundNormal.Y, precision: 3);
        Assert.InRange(
            actual: c.Position.Y - CharacterHalf.Y,
            low: -1e-4f,
            high: c.SkinWidth + 1e-3f
        );
        Assert.Equal(
            expected: 0f,
            actual: c.Velocity.Y,
            precision: 3
        ); // impact velocity absorbed by the surface
    }

    // ── Walking ──────────────────────────────────────────────────────────────

    [Fact]
    public void WalksFlatGround_AtConstantSpeed()
    {
        var world = FlatGround(out _);
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 0.6f));
        Settle(c);

        for (int i = 0; i < 120; i++)
        {
            float prevX = c.Position.X;
            Tick(c: c, vx: 3f);
            Assert.True(condition: c.IsGrounded, userMessage: $"lost ground on tick {i}");
            Assert.Equal(expected: 3f * Dt, actual: c.Position.X - prevX, precision: 4);
        }
    }

    [Fact]
    public void WalksAcrossThreeAdjacentTiles_WithoutSnagging()
    {
        // Three edge-sharing tiles, tops flush at y = 0, spanning x ∈ [−3, 3].
        var world = new CollisionWorld2D();
        world.AddBox(center: new Vec2(x: -2f, y: -0.5f), halfExtents: new Vec2(x: 1f, y: 0.5f));
        world.AddBox(center: new Vec2(x: 0f, y: -0.5f), halfExtents: new Vec2(x: 1f, y: 0.5f));
        world.AddBox(center: new Vec2(x: 2f, y: -0.5f), halfExtents: new Vec2(x: 1f, y: 0.5f));
        var c = Spawn(world: world, position: new Vec2(x: -2.5f, y: 0.6f));
        Settle(c);
        Assert.True(c.IsGrounded);

        for (int i = 0; i < 150; i++)
        {
            float prevX = c.Position.X;
            Tick(c: c, vx: 4f);
            Assert.True(
                condition: c.IsGrounded,
                userMessage: $"lost ground on tick {i} at x={c.Position.X}"
            );
            Assert.False(
                condition: c.IsOnWall,
                userMessage: $"ghost wall on tick {i} at x={c.Position.X}"
            );
            Assert.True(
                condition: c.Position.X - prevX >= (4f * Dt) - 1e-4f,
                userMessage:
                $"snagged on tick {i}: moved {c.Position.X - prevX} at x={c.Position.X}"
            );
        }

        Assert.Equal(expected: 2.5f, actual: c.Position.X, precision: 2);
    }

    // ── Walls & ceilings ─────────────────────────────────────────────────────

    [Fact]
    public void SlidesAlongWall_XStops_YContinues()
    {
        var world = new CollisionWorld2D();
        world.AddBox(
            center: new Vec2(x: 3.9f, y: 0f),
            halfExtents: new Vec2(x: 0.5f, y: 5f)
        ); // left face at x = 3.4
        var c = Spawn(world: world, position: new Vec2(x: 2f, y: 3f));

        int contact = -1;
        for (int i = 0; i < 60; i++)
        {
            float prevY = c.Position.Y;
            c.Velocity = new Vec2(
                x: 5f,
                y: c.Velocity.Y - (Gravity * Dt)
            ); // hold "right" while falling
            c.Move(Dt);
            if (contact < 0 && c.IsOnWall) contact = i;
            if (contact < 0) continue;

            Assert.True(condition: c.IsOnWall, userMessage: $"wall contact lost on tick {i}");
            Assert.False(c.IsGrounded);
            Assert.Equal(
                expected: 3f - c.SkinWidth,
                actual: c.Position.X,
                precision: 3
            ); // pinned a skin off the face
            Assert.Equal(expected: 0f, actual: c.Velocity.X, precision: 4);
            Assert.True(
                condition: c.Position.Y < prevY,
                userMessage: $"Y stopped falling on tick {i}"
            );
        }

        Assert.True(condition: contact >= 0, userMessage: "never touched the wall");
    }

    [Fact]
    public void Ceiling_StopsUpwardMotion_AndSetsFlag()
    {
        var world = FlatGround(out _);
        world.AddBox(
            center: new Vec2(x: 0f, y: 3.5f),
            halfExtents: new Vec2(x: 2f, y: 0.5f)
        ); // underside at y = 3
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 0.6f));
        Settle(c);

        c.Velocity = new Vec2(x: 0f, y: 12f);
        int bonked = -1;
        for (int i = 0; i < 120 && bonked < 0; i++)
        {
            Tick(c);
            if (c.IsOnCeiling) bonked = i;
        }

        Assert.True(condition: bonked >= 0, userMessage: "never reached the ceiling");
        Assert.True(
            condition: c.Velocity.Y <= 0f,
            userMessage: "upward velocity survived the ceiling"
        );
        Assert.True(
            condition: c.Position.Y + CharacterHalf.Y <= 3f + 1e-3f,
            userMessage: $"head inside ceiling at {c.Position.Y}"
        );

        for (int i = 0; i < 200 && !c.IsGrounded; i++) Tick(c);
        Assert.True(condition: c.IsGrounded, userMessage: "did not fall back to the ground");
    }

    // ── Slopes (large circle = curved slope; normal varies with position) ────

    [Fact]
    public void Ascends30DegreeSlope_GroundedThroughout()
    {
        var world = SlopeCircle();
        var c = Spawn(
            world: world,
            position: new Vec2(x: 10.4f, y: -1.9f)
        ); // ~30° flank, box corner contact
        Settle(c);
        Assert.True(c.IsGrounded);
        Assert.True(
            condition: c.GroundNormal.Y < 0.95f,
            userMessage: "expected a sloped ground normal"
        );

        float startX = c.Position.X;
        float startY = c.Position.Y;
        for (int i = 0; i < 90; i++)
        {
            Tick(c: c, vx: -2f); // toward the crest
            Assert.True(
                condition: c.IsGrounded,
                userMessage: $"lost ground on tick {i} at x={c.Position.X}"
            );
        }

        Assert.True(condition: c.Position.X < startX - 1f, userMessage: "made no uphill progress");
        Assert.True(condition: c.Position.Y > startY + 0.3f, userMessage: "did not climb");
    }

    [Fact]
    public void Descends30DegreeSlope_SnapKeepsItGlued()
    {
        var world = SlopeCircle();
        var c = Spawn(world: world, position: new Vec2(x: 3.872f, y: 0.5f)); // ~10° near the crest
        Settle(c);
        Assert.True(c.IsGrounded);

        float startX = c.Position.X;
        float startY = c.Position.Y;
        for (int i = 0; i < 100; i++)
        {
            Tick(c: c, vx: 3f); // away from the crest — surface falls away underfoot
            Assert.True(
                condition: c.IsGrounded,
                userMessage: $"came unglued on tick {i} at x={c.Position.X}"
            );
        }

        Assert.True(
            condition: c.Position.X > startX + 1.5f,
            userMessage: "made no downhill progress"
        );
        Assert.True(condition: c.Position.Y < startY - 0.3f, userMessage: "did not descend");
    }

    [Fact]
    public void SteepSlope70Degrees_DoesNotGround_Slides()
    {
        var world = SlopeCircle();
        // ~70° flank: normal.Y ≈ cos70° ≈ 0.342 < cos50° — unwalkable.
        var c = Spawn(world: world, position: new Vec2(x: 19.194f, y: -12.4f));

        bool touchedWall = false;
        float startX = c.Position.X;
        for (int i = 0; i < 50; i++)
        {
            Tick(c);
            Assert.False(
                condition: c.IsGrounded,
                userMessage: $"grounded on a 70° surface on tick {i}"
            );
            touchedWall |= c.IsOnWall;
        }

        Assert.True(
            condition: touchedWall,
            userMessage: "never registered the steep surface as a wall"
        );
        Assert.True(condition: c.Position.X > startX, userMessage: "did not slide down the flank");
    }

    // ── Jumping & snap interaction ───────────────────────────────────────────

    [Fact]
    public void Jump_IsNotSnappedBackToGround()
    {
        var world = FlatGround(out _);
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 0.6f));
        Settle(c);
        float groundY = c.Position.Y;

        c.Velocity = new Vec2(x: 0f, y: 8f);
        c.Move(Dt);
        Assert.False(condition: c.IsGrounded, userMessage: "jump was snapped back");
        Assert.Equal(expected: groundY + (8f * Dt), actual: c.Position.Y, precision: 3);

        for (int i = 0; i < 10; i++)
        {
            float prevY = c.Position.Y;
            Tick(c);
            Assert.True(
                condition: c.Position.Y > prevY,
                userMessage: $"stopped rising on tick {i}"
            );
            Assert.False(c.IsGrounded);
        }
    }

    [Fact]
    public void TimeSinceGrounded_AccumulatesInAir_ResetsOnLanding()
    {
        var world = FlatGround(out _);
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 0.6f));
        Settle(c);
        Assert.Equal(expected: 0f, actual: c.TimeSinceGrounded);

        c.Velocity = new Vec2(x: 0f, y: 6f);
        c.Move(Dt);
        for (int i = 0; i < 29; i++) Tick(c);
        Assert.False(c.IsGrounded);
        Assert.Equal(
            expected: 30f * Dt,
            actual: c.TimeSinceGrounded,
            precision: 3
        ); // the coyote-time building block

        for (int i = 0; i < 200 && !c.IsGrounded; i++) Tick(c);
        Assert.True(c.IsGrounded);
        Assert.Equal(expected: 0f, actual: c.TimeSinceGrounded);
    }

    // ── One-way platforms ────────────────────────────────────────────────────

    [Fact]
    public void OneWay_LandsWhenFallingFromAbove()
    {
        var world = FlatGround(out _);
        var plat = world.AddBox(
            center: new Vec2(x: 0f, y: 2f),
            halfExtents: new Vec2(x: 2f, y: 0.1f),
            oneWayUp: true
        ); // top at 2.1
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 3.5f));
        Settle(c: c, ticks: 200);

        Assert.True(c.IsGrounded);
        Assert.Equal(expected: plat, actual: c.GroundCollider);
        Assert.Equal(
            expected: 2.1f + c.SkinWidth,
            actual: c.Position.Y - CharacterHalf.Y,
            precision: 2
        );
    }

    [Fact]
    public void OneWay_JumpUpThroughIt_Unimpeded_ThenLandsOnIt()
    {
        var world = FlatGround(out var ground);
        var plat = world.AddBox(
            center: new Vec2(x: 0f, y: 2f),
            halfExtents: new Vec2(x: 2f, y: 0.1f),
            oneWayUp: true
        );
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 0.6f));
        Settle(c);
        Assert.Equal(expected: ground, actual: c.GroundCollider);

        c.Velocity = new Vec2(x: 0f, y: 14f);
        c.Move(Dt);
        bool rosePastPlatform = false;
        for (int i = 0; i < 400 && !c.IsGrounded; i++)
        {
            Tick(c);
            Assert.False(
                condition: c.IsOnCeiling,
                userMessage: $"one-way blocked the ascent on tick {i}"
            );
            if (c.Position.Y - CharacterHalf.Y > 2.2f) rosePastPlatform = true;
        }

        Assert.True(condition: rosePastPlatform, userMessage: "jump never cleared the platform");
        Assert.True(c.IsGrounded);
        Assert.Equal(
            expected: plat,
            actual: c.GroundCollider
        ); // came back down onto the platform's top
    }

    [Fact]
    public void OneWay_DropThrough_FallsThrough_AndIgnoreAutoClears()
    {
        var world = FlatGround(out var ground);
        var plat = world.AddBox(
            center: new Vec2(x: 0f, y: 2f),
            halfExtents: new Vec2(x: 2f, y: 0.1f),
            oneWayUp: true
        );
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 3.5f));
        Settle(c: c, ticks: 200);
        Assert.Equal(expected: plat, actual: c.GroundCollider);

        c.DropThrough();
        for (int i = 0; i < 300 && c.GroundCollider != ground; i++) Tick(c);
        Assert.True(c.IsGrounded);
        Assert.Equal(
            expected: ground,
            actual: c.GroundCollider
        ); // fell through onto the real floor
        Assert.InRange(
            actual: c.Position.Y - CharacterHalf.Y,
            low: -1e-4f,
            high: c.SkinWidth + 1e-3f
        );

        // The ignore must have cleared once fully past: a fresh jump lands back ON the platform.
        c.Velocity = new Vec2(x: 0f, y: 14f);
        c.Move(Dt);
        for (int i = 0; i < 400 && !c.IsGrounded; i++) Tick(c);
        Assert.Equal(expected: plat, actual: c.GroundCollider);
    }

    // ── Moving platforms ─────────────────────────────────────────────────────

    [Fact]
    public void MovingPlatform_CarriesHorizontally()
    {
        var world = new CollisionWorld2D();
        var plat = world.AddBox(
            center: new Vec2(x: 0f, y: 1f),
            halfExtents: new Vec2(x: 1.5f, y: 0.25f)
        ); // top at 1.25
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 1.8f));
        Settle(c);
        Assert.Equal(expected: plat, actual: c.GroundCollider);

        float px = 0f;
        for (int i = 0; i < 120; i++)
        {
            world.BeginStep();
            px += 2f * Dt;
            world.SetPosition(handle: plat, position: new Vec2(x: px, y: 1f));
            Tick(c);
            Assert.True(condition: c.IsGrounded, userMessage: $"fell off the conveyor on tick {i}");
        }

        Assert.Equal(expected: px, actual: c.Position.X, precision: 2); // rode the full 2 units
    }

    [Fact]
    public void MovingPlatform_ElevatorCarriesVertically_Over200Ticks()
    {
        var world = new CollisionWorld2D();
        var plat = world.AddBox(
            center: new Vec2(x: 0f, y: 1f),
            halfExtents: new Vec2(x: 1.5f, y: 0.25f)
        );
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 1.8f));
        Settle(c);
        Assert.Equal(expected: plat, actual: c.GroundCollider);

        float py = 1f;
        for (int i = 0; i < 200; i++)
        {
            world.BeginStep();
            py += 1.5f * Dt; // ascending elevator
            world.SetPosition(handle: plat, position: new Vec2(x: 0f, y: py));
            Tick(c);
            Assert.True(condition: c.IsGrounded, userMessage: $"lost the elevator on tick {i}");
            float gap = c.Position.Y - CharacterHalf.Y - (py + 0.25f);
            Assert.InRange(actual: gap, low: -0.005f, high: 0.03f);
        }

        for (int i = 0; i < 100; i++)
        {
            world.BeginStep();
            py -= 1.5f * Dt; // and back down
            world.SetPosition(handle: plat, position: new Vec2(x: 0f, y: py));
            Tick(c);
            Assert.True(
                condition: c.IsGrounded,
                userMessage: $"lost the descending elevator on tick {i}"
            );
        }

        Assert.Equal(
            expected: py + 0.25f + c.SkinWidth,
            actual: c.Position.Y - CharacterHalf.Y,
            precision: 2
        );
    }

    // ── Triggers & masks ─────────────────────────────────────────────────────

    [Fact]
    public void Triggers_EnterAndExit_FireExactlyOnce()
    {
        var world = FlatGround(out _);
        var zone = world.AddBox(
            center: new Vec2(x: 2.5f, y: 0.5f),
            halfExtents: new Vec2(x: 0.5f, y: 1f),
            isTrigger: true
        );
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 0.6f));
        Settle(c);

        int enters = 0, exits = 0;
        for (int i = 0; i < 150; i++)
        {
            Tick(c: c, vx: 4f);
            if (c.TriggersEntered.Count > 0)
            {
                enters += c.TriggersEntered.Count;
                Assert.Equal(expected: zone, actual: c.TriggersEntered[0]);
            }

            if (c.TriggersExited.Count > 0)
            {
                exits += c.TriggersExited.Count;
                Assert.Equal(expected: zone, actual: c.TriggersExited[0]);
            }
        }

        Assert.True(c.Position.X > 4f); // walked all the way through
        Assert.Equal(expected: 1, actual: enters);
        Assert.Equal(expected: 1, actual: exits);
    }

    [Fact]
    public void LayerMask_PassesThroughCollidersOutsideTheMask()
    {
        var world = FlatGround(out _); // ground is layer 1
        world.AddBox(
            center: new Vec2(x: 2f, y: 1.5f),
            halfExtents: new Vec2(x: 0.2f, y: 2f),
            layer: 2
        ); // a wall the mask excludes
        var c = Spawn(world: world, position: new Vec2(x: 0f, y: 0.6f));
        c.CollisionMask = 1;
        Settle(c);

        for (int i = 0; i < 90; i++)
        {
            Tick(c: c, vx: 4f);
            Assert.True(c.IsGrounded);
            Assert.False(c.IsOnWall);
        }

        Assert.True(
            condition: c.Position.X > 2.4f,
            userMessage: $"was blocked at x={c.Position.X}"
        ); // walked straight through
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CollisionWorld2D FlatGround(out ColliderHandle ground)
    {
        var world = new CollisionWorld2D();
        ground = world.AddBox(
            center: new Vec2(x: 0f, y: -0.5f),
            halfExtents: new Vec2(x: 50f, y: 0.5f)
        ); // top at y = 0
        return world;
    }

    /// <summary>
    ///     R=20 circle whose top touches the origin — a curved hill. On the right flank the surface
    ///     normal tilts with x, so one shape provides 10°/30°/70° "slopes" for the walkability tests.
    /// </summary>
    private static CollisionWorld2D SlopeCircle()
    {
        var world = new CollisionWorld2D();
        world.AddCircle(center: new Vec2(x: 0f, y: -20f), radius: 20f);
        return world;
    }

    private static CharacterController2D Spawn(CollisionWorld2D world, Vec2 position) =>
        new(world: world, halfExtents: CharacterHalf) { Position = position };

    /// <summary>One fixed game tick: the game integrates gravity into Velocity, then the controller moves.</summary>
    private static void Tick(CharacterController2D c, float vx = 0f)
    {
        c.Velocity = new Vec2(x: vx, y: c.Velocity.Y - (Gravity * Dt));
        c.Move(Dt);
    }

    private static void Settle(CharacterController2D c, int ticks = 60)
    {
        for (int i = 0; i < ticks; i++) Tick(c);
    }
}
