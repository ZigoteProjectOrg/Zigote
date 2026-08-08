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
                Vec3.Zero,
                Vec3.Forward,
                10f,
                out var hit
            )
        );
        Assert.Equal(default, hit.Body);
        Assert.Null(Physics.Raycast(Vec3.Zero, Vec3.Forward, 10f));
    }

    [Fact]
    public void TryRaycast_Routes_Through_The_Backend_And_Fills_The_Hit()
    {
        var fake = new FakeBackend {
            HasHit = true,
            Hit = new RaycastHit3D(
                new RigidBodyHandle(7),
                new Vec3(1, 2, 3),
                Vec3.Up,
                4.5f
            ),
        };
        Physics.Backend = fake;
        try
        {
            var ignore = new RigidBodyHandle(42);
            Assert.True(
                Physics.TryRaycast(
                    Vec3.Zero,
                    Vec3.Forward,
                    10f,
                    ignore,
                    out var hit
                )
            );
            Assert.Equal(7u, hit.Body.BodyId);
            Assert.Equal(new Vec3(1, 2, 3), hit.Point);
            Assert.Equal(Vec3.Up, hit.Normal);
            Assert.Equal(4.5f, hit.Distance);
            Assert.Equal(ignore, fake.LastIgnore);

            // The no-ignore overload passes None, never a valid body id.
            Assert.True(
                Physics.TryRaycast(
                    Vec3.Zero,
                    Vec3.Forward,
                    10f,
                    out _
                )
            );
            Assert.Equal(RigidBodyHandle.None, fake.LastIgnore);
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
                new RigidBodyHandle(3),
                new Vec3(0, 1, 0),
                Vec3.Up,
                2f
            ),
        };
        Physics.Backend = fake;
        try
        {
            var hit = Physics.Raycast(Vec3.Zero, Vec3.Forward, 10f);
            Assert.NotNull(hit);
            Assert.Equal(3u, hit!.Body.BodyId);
            Assert.Equal(new Vec3(0, 1, 0), hit.Point);
            Assert.Equal(Vec3.Up, hit.Normal);
            Assert.Equal(2f, hit.Distance);

            fake.HasHit = false;
            Assert.Null(Physics.Raycast(Vec3.Zero, Vec3.Forward, 10f));
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
            Vec3 eulerRotation, float mass, bool dynamic)
        {
            return RigidBodyHandle.None;
        }

        public void DestroyBody(RigidBodyHandle body)
        {
        }

        public Vec3 GetPosition(RigidBodyHandle body)
        {
            return Vec3.Zero;
        }

        public void SetPosition(RigidBodyHandle body, Vec3 position)
        {
        }

        public Quat GetRotation(RigidBodyHandle body)
        {
            return Quat.Identity;
        }

        public void SetRotation(RigidBodyHandle body, Quat rotation)
        {
        }

        public Vec3 GetLinearVelocity(RigidBodyHandle body)
        {
            return Vec3.Zero;
        }

        public void SetLinearVelocity(RigidBodyHandle body, Vec3 velocity)
        {
        }

        public Vec3 GetAngularVelocity(RigidBodyHandle body)
        {
            return Vec3.Zero;
        }

        public void SetAngularVelocity(RigidBodyHandle body, Vec3 velocity)
        {
        }

        public void AddForce(RigidBodyHandle body, Vec3 force)
        {
        }

        public void AddForceAtPoint(RigidBodyHandle body, Vec3 force, Vec3 worldPoint)
        {
        }

        public void AddTorque(RigidBodyHandle body, Vec3 torque)
        {
        }

        public void AddImpulse(RigidBodyHandle body, Vec3 impulse)
        {
        }

        public bool TryRaycast(Vec3 origin, Vec3 direction, float maxDistance,
            RigidBodyHandle ignore, out RaycastHit3D hit)
        {
            LastIgnore = ignore;
            hit = Hit;
            return HasHit;
        }
    }
}