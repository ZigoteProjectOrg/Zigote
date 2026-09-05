using Xunit;
using Zigote.Core.Math3D;
using Zigote.Core.Physics;
using Zigote.Scripting;

namespace Zigote.Tests;

/// <summary>
///     The generic Physics scripting provider's ray-cast surface: the allocation-free
///     <c>TryRaycast</c> (out-param struct hit) and the class-returning <c>Raycast</c> compatibility
///     wrapper over it, via a fake backend — no native world needed.
/// </summary>
public class PhysicsProviderTests
{
    [Fact]
    public void TryRaycast_Is_A_Safe_Miss_Without_A_Backend()
    {
        Physics.Backend = null;

        Assert.False(
            Physics.TryRaycast(
                origin: Vec3.Zero,
                direction: Vec3.Forward,
                maxDistance: 10f,
                hit: out var hit
            )
        );
        Assert.Equal(expected: default, actual: hit.Body);
        Assert.Null(Physics.Raycast(origin: Vec3.Zero, direction: Vec3.Forward, maxDistance: 10f));
    }

    [Fact]
    public void TryRaycast_Routes_Through_The_Backend_And_Fills_The_Hit()
    {
        var fake = new FakeBackend {
            HasHit = true,
            Hit = new RaycastHit3D(
                body: new RigidBodyHandle(7),
                point: new Vec3(x: 1, y: 2, z: 3),
                normal: Vec3.Up,
                distance: 4.5f
            ),
        };
        Physics.Backend = fake;
        try
        {
            var ignore = new RigidBodyHandle(42);
            Assert.True(
                Physics.TryRaycast(
                    origin: Vec3.Zero,
                    direction: Vec3.Forward,
                    maxDistance: 10f,
                    ignore: ignore,
                    hit: out var hit
                )
            );
            Assert.Equal(expected: 7u, actual: hit.Body.BodyId);
            Assert.Equal(expected: new Vec3(x: 1, y: 2, z: 3), actual: hit.Point);
            Assert.Equal(expected: Vec3.Up, actual: hit.Normal);
            Assert.Equal(expected: 4.5f, actual: hit.Distance);
            Assert.Equal(expected: ignore, actual: fake.LastIgnore);

            // The no-ignore overload passes None, never a valid body id.
            Assert.True(
                Physics.TryRaycast(
                    origin: Vec3.Zero,
                    direction: Vec3.Forward,
                    maxDistance: 10f,
                    hit: out _
                )
            );
            Assert.Equal(expected: RigidBodyHandle.None, actual: fake.LastIgnore);
        }
        finally
        {
            Physics.Backend = null;
        }
    }

    [Fact]
    public void Raycast_Wrapper_Mirrors_TryRaycast()
    {
        var fake = new FakeBackend {
            HasHit = true,
            Hit = new RaycastHit3D(
                body: new RigidBodyHandle(3),
                point: new Vec3(x: 0, y: 1, z: 0),
                normal: Vec3.Up,
                distance: 2f
            ),
        };
        Physics.Backend = fake;
        try
        {
            var hit = Physics.Raycast(origin: Vec3.Zero, direction: Vec3.Forward, maxDistance: 10f);
            Assert.NotNull(hit);
            Assert.Equal(expected: 3u, actual: hit!.Body.BodyId);
            Assert.Equal(expected: new Vec3(x: 0, y: 1, z: 0), actual: hit.Point);
            Assert.Equal(expected: Vec3.Up, actual: hit.Normal);
            Assert.Equal(expected: 2f, actual: hit.Distance);

            fake.HasHit = false;
            Assert.Null(
                Physics.Raycast(origin: Vec3.Zero, direction: Vec3.Forward, maxDistance: 10f)
            );
        }
        finally
        {
            Physics.Backend = null;
        }
    }

    private sealed class FakeBackend : IPhysicsBackend
    {
        public bool HasHit;
        public RaycastHit3D Hit;
        public RigidBodyHandle LastIgnore;

        public RigidBodyHandle CreateBody(PhysicsShapeType shape, Vec3 halfExtents, Vec3 position,
            Vec3 eulerRotation, float mass, bool dynamic) =>
            RigidBodyHandle.None;

        public void DestroyBody(RigidBodyHandle body) { }

        public Vec3 GetPosition(RigidBodyHandle body) => Vec3.Zero;

        public void SetPosition(RigidBodyHandle body, Vec3 position) { }

        public Quat GetRotation(RigidBodyHandle body) => Quat.Identity;

        public void SetRotation(RigidBodyHandle body, Quat rotation) { }

        public Vec3 GetLinearVelocity(RigidBodyHandle body) => Vec3.Zero;

        public void SetLinearVelocity(RigidBodyHandle body, Vec3 velocity) { }

        public Vec3 GetAngularVelocity(RigidBodyHandle body) => Vec3.Zero;

        public void SetAngularVelocity(RigidBodyHandle body, Vec3 velocity) { }

        public void AddForce(RigidBodyHandle body, Vec3 force) { }

        public void AddForceAtPoint(RigidBodyHandle body, Vec3 force, Vec3 worldPoint) { }

        public void AddTorque(RigidBodyHandle body, Vec3 torque) { }

        public void AddImpulse(RigidBodyHandle body, Vec3 impulse) { }

        public bool TryRaycast(Vec3 origin, Vec3 direction, float maxDistance,
            RigidBodyHandle ignore, out RaycastHit3D hit)
        {
            LastIgnore = ignore;
            hit = Hit;
            return HasHit;
        }
    }
}

/// <summary>
///     The Jolt worker-thread count derived from the body budget. The invariant that matters: a
///     small world must not get a wide barrier — <c>-1</c> (Jolt's own auto, every core but one) is
///     what this replaced, and it barriered sixteen threads for a scene with forty crates, twice
///     per rendered frame at the 120 Hz fixed step.
/// </summary>
public class PhysicsWorkerCountTests
{
    private static int Cores => Math.Max(val1: 1, val2: Environment.ProcessorCount - 1);

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(255u)]
    public void SmallWorlds_GetOneWorker(uint bodies) =>
        Assert.Equal(expected: 1, actual: PhysicsWorld.WorkerCount(bodies));

    [Fact]
    public void TheDefaultBudget_ScalesWithBodies() => Assert.Equal(
        expected: Math.Min(val1: 4, val2: Cores),
        actual: PhysicsWorld.WorkerCount(1024) // the Initialize default
    );

    [Fact]
    public void HugeWorlds_LeaveTheFrameThreadACore() =>
        Assert.Equal(expected: Cores, actual: PhysicsWorld.WorkerCount(uint.MaxValue));

    [Fact]
    public void TheCountNeverShrinksAsBodiesGrow()
    {
        int previous = 0;
        for (uint bodies = 0; bodies < 8192; bodies += 97)
        {
            int n = PhysicsWorld.WorkerCount(bodies);
            Assert.True(condition: n >= previous, userMessage: $"{bodies} bodies dropped to {n}");
            previous = n;
        }
    }
}
