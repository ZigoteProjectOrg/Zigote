namespace Zigote.Core.Math3D;

public readonly struct Transform3D(Vec3 position, Quat rotation, Vec3 scale)
    : IEquatable<Transform3D>
{
    public Vec3 Position { get; } = position;
    public Quat Rotation { get; } = rotation;
    public Vec3 Scale { get; } = scale;

    public static readonly Transform3D Identity = new(Vec3.Zero, Quat.Identity, Vec3.One);

    public Mat4 ToMat4()
    {
        // Closed-form TRS. Translation(p) * Rotation(r) * Scaling(s) reduces algebraically to scaling
        // the rotation's basis columns and placing the translation in the last column — the eliminated
        // terms are all ×0/+0, so this is bit-identical to `p * r * s` but skips its two full 4×4
        // products on this per-node-per-frame path.
        var r = Rotation.ToMat4();
        return new Mat4(
            r.Col0 * Scale.X,
            r.Col1 * Scale.Y,
            r.Col2 * Scale.Z,
            new Vec4(
                Position.X,
                Position.Y,
                Position.Z,
                1f
            )
        );
    }

    public static Transform3D Lerp(Transform3D a, Transform3D b, float t)
    {
        return new Transform3D(
            a.Position.Lerp(b.Position, t),
            Quat.Slerp(a.Rotation, b.Rotation, t),
            a.Scale.Lerp(b.Scale, t)
        );
    }

    /// <summary>Combines parent and child transforms (TRS order, child relative to parent).</summary>
    public static Transform3D Combine(Transform3D parent, Transform3D child)
    {
        return new Transform3D(
            parent.Position + parent.Rotation.RotateVec(child.Position * parent.Scale),
            parent.Rotation * child.Rotation,
            parent.Scale * child.Scale
        );
    }

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(Transform3D other)
    {
        return Position == other.Position && Rotation == other.Rotation && Scale == other.Scale;
    }

    public override bool Equals(object? obj)
    {
        return obj is Transform3D t && Equals(t);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Position, Rotation, Scale);
    }

    public override string ToString()
    {
        return $"Transform3D(pos={Position}, rot={Rotation}, scale={Scale})";
    }

    public static bool operator ==(Transform3D a, Transform3D b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Transform3D a, Transform3D b)
    {
        return !a.Equals(b);
    }
}