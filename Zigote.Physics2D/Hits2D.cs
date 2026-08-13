using Zigote.Core.Math3D;

namespace Zigote.Physics2D;

/// <summary>Closest hit of a <see cref="CollisionWorld2D.Raycast" />. Distance is in world units along the ray.</summary>
public readonly struct RayHit2D(ColliderHandle collider, Vec2 point, Vec2 normal, float distance)
{
    public ColliderHandle Collider { get; } = collider;
    public Vec2 Point { get; } = point;
    public Vec2 Normal { get; } = normal;
    public float Distance { get; } = distance;
}

/// <summary>
///     First time-of-impact of a <see cref="CollisionWorld2D.SweepBox" />. Time is the normalized fraction
///     of the displacement in [0,1] (0 = already touching at the start). Normal points away from the hit
///     collider, toward the mover.
/// </summary>
public readonly struct SweepHit2D(ColliderHandle collider, float time, Vec2 point, Vec2 normal)
{
    public ColliderHandle Collider { get; } = collider;
    public float Time { get; } = time;
    public Vec2 Point { get; } = point;
    public Vec2 Normal { get; } = normal;
}
