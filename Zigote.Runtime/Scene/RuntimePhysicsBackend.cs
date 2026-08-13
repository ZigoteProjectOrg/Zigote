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
        uint id = physics.CreateAndAddBody(
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

    public Vec3 GetPosition(RigidBodyHandle body) => physics.GetBodyPosition(body.BodyId);

    public void SetPosition(RigidBodyHandle body, Vec3 position) =>
        physics.SetBodyPosition(bodyId: body.BodyId, position: position);

    public Quat GetRotation(RigidBodyHandle body) => physics.GetBodyRotationQuat(body.BodyId);

    public void SetRotation(RigidBodyHandle body, Quat rotation) =>
        physics.SetBodyRotationQuat(bodyId: body.BodyId, rotation: rotation);

    public Vec3 GetLinearVelocity(RigidBodyHandle body) => physics.GetLinearVelocity(body.BodyId);

    public void SetLinearVelocity(RigidBodyHandle body, Vec3 v) =>
        physics.SetLinearVelocity(bodyId: body.BodyId, velocity: v);

    public Vec3 GetAngularVelocity(RigidBodyHandle body) => physics.GetAngularVelocity(body.BodyId);

    public void SetAngularVelocity(RigidBodyHandle body, Vec3 v) =>
        physics.SetAngularVelocity(bodyId: body.BodyId, velocity: v);

    public void AddForce(RigidBodyHandle body, Vec3 force) =>
        physics.AddForce(bodyId: body.BodyId, force: force);

    public void AddForceAtPoint(RigidBodyHandle body, Vec3 force, Vec3 worldPoint) =>
        physics.AddForceAtPoint(bodyId: body.BodyId, force: force, worldPoint: worldPoint);

    public void AddTorque(RigidBodyHandle body, Vec3 torque) =>
        physics.AddTorque(bodyId: body.BodyId, torque: torque);

    public void AddImpulse(RigidBodyHandle body, Vec3 impulse) =>
        physics.AddImpulse(bodyId: body.BodyId, impulse: impulse);

    public bool TryRaycast(Vec3 origin, Vec3 direction, float maxDistance,
        RigidBodyHandle ignore, out RaycastHit3D hit)
    {
        if (!physics.Raycast(
                origin: origin,
                direction: direction,
                maxDistance: maxDistance,
                hitBody: out uint body,
                point: out var point,
                normal: out var normal,
                distance: out float distance,
                ignoreBody: ignore.BodyId
            ))
        {
            hit = default;
            return false;
        }

        hit = new RaycastHit3D(
            body: new RigidBodyHandle(body),
            point: point,
            normal: normal,
            distance: distance
        );
        return true;
    }

    /// <summary>Enumerate live script-created bodies with their current world transform.</summary>
    public IEnumerable<DebugBody> DebugBodies()
    {
        foreach ((uint id, var info) in _shapes)
        {
            yield return new DebugBody(
                Shape: info.Shape,
                HalfExtents: info.HalfExtents,
                Position: physics.GetBodyPosition(id),
                Rotation: physics.GetBodyRotationQuat(id)
            );
        }
    }

    /// <summary>A script-created body's collision shape + its live world transform, for debug drawing.</summary>
    public readonly record struct DebugBody(
        PhysicsShapeType Shape,
        Vec3 HalfExtents,
        Vec3 Position,
        Quat Rotation);
}
