using Zigote.Core.Math3D;
using Zigote.Core.Physics;

namespace Zigote.Scripting;

/// <summary>A lightweight, copyable handle to a rigid body owned by the physics world.</summary>
public readonly struct RigidBodyHandle(uint bodyId) : IEquatable<RigidBodyHandle>
{
    public const uint Invalid = 0xFFFF_FFFF;
    public static RigidBodyHandle None => new(Invalid);

    public uint BodyId { get; } = bodyId;
    public bool IsValid => BodyId != Invalid;

    public bool Equals(RigidBodyHandle other)
    {
        return BodyId == other.BodyId;
    }

    public override bool Equals(object? obj)
    {
        return obj is RigidBodyHandle h && Equals(h);
    }

    public override int GetHashCode()
    {
        return (int)BodyId;
    }

    public static bool operator ==(RigidBodyHandle a, RigidBodyHandle b)
    {
        return a.BodyId == b.BodyId;
    }

    public static bool operator !=(RigidBodyHandle a, RigidBodyHandle b)
    {
        return a.BodyId != b.BodyId;
    }
}

/// <summary>Result of a successful <see cref="Physics.Raycast" />.</summary>
public sealed class RaycastHit
{
    public RigidBodyHandle Body;
    public float Distance;
    public Vec3 Normal;
    public Vec3 Point;
}

/// <summary>
///     Closest hit of a <see cref="Physics.TryRaycast" />. Distance is in world units along the ray.
///     The allocation-free counterpart of <see cref="RaycastHit" /> for per-tick casts.
/// </summary>
public readonly struct RaycastHit3D(RigidBodyHandle body, Vec3 point, Vec3 normal, float distance)
{
    public RigidBodyHandle Body { get; } = body;
    public Vec3 Point { get; } = point;
    public Vec3 Normal { get; } = normal;
    public float Distance { get; } = distance;
}

/// <summary>
///     The contract the host (editor play session / game runtime) implements to back the generic
///     <see cref="Physics" /> scripting API with a real physics world. A strongly-typed interface
///     (rather
///     than multiplexed delegates) so it stays debuggable and headless tests can inject a fake
///     backend.
/// </summary>
public interface IPhysicsBackend
{
    RigidBodyHandle CreateBody(PhysicsShapeType shape, Vec3 halfExtents, Vec3 position,
        Vec3 eulerRotation,
        float mass, bool dynamic);

    void DestroyBody(RigidBodyHandle body);

    Vec3 GetPosition(RigidBodyHandle body);
    void SetPosition(RigidBodyHandle body, Vec3 position);
    Quat GetRotation(RigidBodyHandle body);
    void SetRotation(RigidBodyHandle body, Quat rotation);

    Vec3 GetLinearVelocity(RigidBodyHandle body);
    void SetLinearVelocity(RigidBodyHandle body, Vec3 velocity);
    Vec3 GetAngularVelocity(RigidBodyHandle body);
    void SetAngularVelocity(RigidBodyHandle body, Vec3 velocity);

    void AddForce(RigidBodyHandle body, Vec3 force);
    void AddForceAtPoint(RigidBodyHandle body, Vec3 force, Vec3 worldPoint);
    void AddTorque(RigidBodyHandle body, Vec3 torque);
    void AddImpulse(RigidBodyHandle body, Vec3 impulse);

    /// <summary>
    ///     Closest-hit ray cast; <paramref name="ignore" /> (if valid) is skipped (e.g. the caster's
    ///     own body). Returns true on hit, filling <paramref name="hit" />.
    /// </summary>
    bool TryRaycast(Vec3 origin, Vec3 direction, float maxDistance, RigidBodyHandle ignore,
        out RaycastHit3D hit);
}

/// <summary>
///     Generic rigid-body physics access for scripts: create/drive bodies, apply forces, and ray-cast
///     the
///     world. Engine-generic — it knows nothing about vehicles. The host assigns
///     <see cref="Backend" /> in
///     play mode (and clears it on stop); outside play every call is a safe no-op. Mirrors
///     <see cref="Input" />.
/// </summary>
public static class Physics
{
    /// <summary>Set by the host (or a test) to route calls to a real physics world.</summary>
    public static IPhysicsBackend? Backend { get; set; }

    public static bool IsAvailable => Backend != null;

    public static RigidBodyHandle CreateBody(PhysicsShapeType shape, Vec3 halfExtents,
        Vec3 position,
        Vec3 eulerRotation, float mass, bool dynamic = true)
    {
        return Backend?.CreateBody(
            shape,
            halfExtents,
            position,
            eulerRotation,
            mass,
            dynamic
        ) ?? RigidBodyHandle.None;
    }

    public static void DestroyBody(RigidBodyHandle body)
    {
        Backend?.DestroyBody(body);
    }

    public static Vec3 GetPosition(RigidBodyHandle body)
    {
        return Backend?.GetPosition(body) ?? Vec3.Zero;
    }

    public static void SetPosition(RigidBodyHandle body, Vec3 position)
    {
        Backend?.SetPosition(body, position);
    }

    public static Quat GetRotation(RigidBodyHandle body)
    {
        return Backend?.GetRotation(body) ?? Quat.Identity;
    }

    public static void SetRotation(RigidBodyHandle body, Quat rotation)
    {
        Backend?.SetRotation(body, rotation);
    }

    public static Vec3 GetLinearVelocity(RigidBodyHandle body)
    {
        return Backend?.GetLinearVelocity(body) ?? Vec3.Zero;
    }

    public static void SetLinearVelocity(RigidBodyHandle body, Vec3 v)
    {
        Backend?.SetLinearVelocity(body, v);
    }

    public static Vec3 GetAngularVelocity(RigidBodyHandle body)
    {
        return Backend?.GetAngularVelocity(body) ?? Vec3.Zero;
    }

    public static void SetAngularVelocity(RigidBodyHandle body, Vec3 v)
    {
        Backend?.SetAngularVelocity(body, v);
    }

    public static void AddForce(RigidBodyHandle body, Vec3 force)
    {
        Backend?.AddForce(body, force);
    }

    public static void AddForceAtPoint(RigidBodyHandle body, Vec3 force, Vec3 worldPoint)
    {
        Backend?.AddForceAtPoint(body, force, worldPoint);
    }

    public static void AddTorque(RigidBodyHandle body, Vec3 torque)
    {
        Backend?.AddTorque(body, torque);
    }

    public static void AddImpulse(RigidBodyHandle body, Vec3 impulse)
    {
        Backend?.AddImpulse(body, impulse);
    }

    /// <summary>Allocation-free closest-hit ray cast: true on hit, filling <paramref name="hit" />.</summary>
    public static bool TryRaycast(Vec3 origin, Vec3 direction, float maxDistance,
        out RaycastHit3D hit)
    {
        return TryRaycast(
            origin,
            direction,
            maxDistance,
            RigidBodyHandle.None,
            out hit
        );
    }

    /// <summary>
    ///     Allocation-free closest-hit ray cast, skipping <paramref name="ignore" /> (if valid) —
    ///     e.g. the caster's own body. Prefer this over <see cref="Raycast(Vec3, Vec3, float)" />
    ///     on per-tick paths: the class result allocates per hit.
    /// </summary>
    public static bool TryRaycast(Vec3 origin, Vec3 direction, float maxDistance,
        RigidBodyHandle ignore, out RaycastHit3D hit)
    {
        if (Backend is { } backend)
            return backend.TryRaycast(
                origin,
                direction,
                maxDistance,
                ignore,
                out hit
            );
        hit = default;
        return false;
    }

    public static RaycastHit? Raycast(Vec3 origin, Vec3 direction, float maxDistance)
    {
        return Raycast(
            origin,
            direction,
            maxDistance,
            RigidBodyHandle.None
        );
    }

    public static RaycastHit? Raycast(Vec3 origin, Vec3 direction, float maxDistance,
        RigidBodyHandle ignore)
    {
        return TryRaycast(
            origin,
            direction,
            maxDistance,
            ignore,
            out var hit
        )
            ? new RaycastHit {
                Body = hit.Body,
                Point = hit.Point,
                Normal = hit.Normal,
                Distance = hit.Distance,
            }
            : null;
    }
}