using Zigote.Core.Math3D;

namespace Zigote.Core.Physics;

public enum PhysicsShapeType : byte
{
    Box = 0,
    Sphere = 1,
    Capsule = 2,
    Cylinder = 3,
}

public enum PhysicsMotionType : byte
{
    Static = 0,
    Kinematic = 1,
    Dynamic = 2,
}

/// <summary>
///     Parameters for creating a physics body.
///     <list type="bullet">
///         <item><b>Box</b>: HalfExtents = (hx, hy, hz)</item>
///         <item><b>Sphere</b>: HalfExtents.X = radius</item>
///         <item><b>Capsule</b>: HalfExtents.X = radius, HalfExtents.Y = half-height of cylinder part</item>
///         <item><b>Cylinder</b>: HalfExtents.X = radius, HalfExtents.Y = half-height</item>
///     </list>
/// </summary>
public struct PhysicsBodySettings
{
    // A transient FFI arg-bag: created inline, unpacked field-by-field into scalar create-body FFI
    // args, never stored/mutated across calls — so a struct removes the per-body heap allocation with
    // no reference-semantics risk. The explicit parameterless ctor makes the property initialisers run
    // under `new PhysicsBodySettings { ... }` object-initialiser syntax (the only way it is built).
    public PhysicsBodySettings()
    {
    }

    public PhysicsShapeType ShapeType { get; set; } = PhysicsShapeType.Box;
    public Vec3 HalfExtents { get; set; } = new(0.5f, 0.5f, 0.5f);
    public Vec3 Position { get; set; } = Vec3.Zero;
    public Vec3 Rotation { get; set; } = Vec3.Zero;
    public PhysicsMotionType MotionType { get; set; } = PhysicsMotionType.Dynamic;
    public float Friction { get; set; } = 0.2f;
    public float Restitution { get; set; } = 0.0f;

    /// <summary>Scales how strongly world gravity affects this body. 1 = normal, 0 = weightless.</summary>
    public float GravityFactor { get; set; } = 1.0f;

    /// <summary>Explicit mass in kg. 0 (or less) lets Jolt derive mass + inertia from the shape.</summary>
    public float Mass { get; set; } = 0.0f;
}
