using Zigote.Core.Math3D;
using Zigote.Core.Physics;
using Zigote.Scripting;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Routes the generic <see cref="Physics" /> scripting API to the play session's JoltPhysics
///     world.
///     Engine-generic — it exposes rigid bodies + ray casts, with no game concepts.
/// </summary>
internal sealed class RuntimePhysicsBackend(PhysicsWorld physics) : IPhysicsBackend
{
    // Shape + half-extents of each live script-created body, so the physics-wireframe overlay can draw
    // bodies made through this generic API (e.g. a vehicle chassis) — they are not node.UsePhysics nodes.
    private readonly Dictionary<uint, (PhysicsShapeType Shape, Vec3 HalfExtents)> _shapes = new();

    public RigidBodyHandle CreateBody(PhysicsShapeType shape, Vec3 halfExtents, Vec3 position,
        Vec3 eulerRotation, float mass, bool dynamic)
    {
        var id = physics.CreateAndAddBody(
            new PhysicsBodySettings {
                ShapeType = shape,
                HalfExtents = halfExtents,
                Position = position,
                Rotation = eulerRotation,
                MotionType = dynamic ? PhysicsMotionType.Dynamic : PhysicsMotionType.Static,
                Mass = mass,
                GravityFactor = dynamic ? 1f : 0f,
            }
        );
        if (id != PhysicsWorld.InvalidBodyId) _shapes[id] = (shape, halfExtents);
        return new RigidBodyHandle(id);
    }

    public void DestroyBody(RigidBodyHandle body)
    {
        _shapes.Remove(body.BodyId);
        physics.DestroyBody(body.BodyId);
    }

    public Vec3 GetPosition(RigidBodyHandle body)
    {
        return physics.GetBodyPosition(body.BodyId);
    }

    public void SetPosition(RigidBodyHandle body, Vec3 position)
    {
        physics.SetBodyPosition(body.BodyId, position);
    }

    public Quat GetRotation(RigidBodyHandle body)
    {
        return physics.GetBodyRotationQuat(body.BodyId);
    }

    public void SetRotation(RigidBodyHandle body, Quat rotation)
    {
        physics.SetBodyRotationQuat(body.BodyId, rotation);
    }

    public Vec3 GetLinearVelocity(RigidBodyHandle body)
    {
        return physics.GetLinearVelocity(body.BodyId);
    }

    public void SetLinearVelocity(RigidBodyHandle body, Vec3 v)
    {
        physics.SetLinearVelocity(body.BodyId, v);
    }

    public Vec3 GetAngularVelocity(RigidBodyHandle body)
    {
        return physics.GetAngularVelocity(body.BodyId);
    }

    public void SetAngularVelocity(RigidBodyHandle body, Vec3 v)
    {
        physics.SetAngularVelocity(body.BodyId, v);
    }

    public void AddForce(RigidBodyHandle body, Vec3 force)
    {
        physics.AddForce(body.BodyId, force);
    }

    public void AddForceAtPoint(RigidBodyHandle body, Vec3 force, Vec3 worldPoint)
    {
        physics.AddForceAtPoint(body.BodyId, force, worldPoint);
    }

    public void AddTorque(RigidBodyHandle body, Vec3 torque)
    {
        physics.AddTorque(body.BodyId, torque);
    }

    public void AddImpulse(RigidBodyHandle body, Vec3 impulse)
    {
        physics.AddImpulse(body.BodyId, impulse);
    }

    public bool TryRaycast(Vec3 origin, Vec3 direction, float maxDistance,
        RigidBodyHandle ignore, out RaycastHit3D hit)
    {
        if (!physics.Raycast(
                origin,
                direction,
                maxDistance,
                out var body,
                out var point,
                out var normal,
                out var distance,
                ignore.BodyId
            ))
        {
            hit = default;
            return false;
        }

        hit = new RaycastHit3D(
            new RigidBodyHandle(body),
            point,
            normal,
            distance
        );
        return true;
    }

    /// <summary>Enumerate live script-created bodies with their current world transform.</summary>
    public IEnumerable<DebugBody> DebugBodies()
    {
        foreach (var (id, info) in _shapes)
            yield return new DebugBody(
                info.Shape,
                info.HalfExtents,
                physics.GetBodyPosition(id),
                physics.GetBodyRotationQuat(id)
            );
    }

    /// <summary>A script-created body's collision shape + its live world transform, for debug drawing.</summary>
    public readonly record struct DebugBody(
        PhysicsShapeType Shape,
        Vec3 HalfExtents,
        Vec3 Position,
        Quat Rotation);
}
