using System.Numerics;

namespace Zigote.Core.Math3D;

/// <summary>
///     Unit quaternion representing a rotation. Stored as (X, Y, Z, W) where W is the scalar
///     part.
/// </summary>
// Backed by System.Numerics.Quaternion (identical X,Y,Z,W layout). The Hamilton product and vector
// rotation lower to SIMD; the convention-specific builders (axis-angle, from-matrix, slerp, to-matrix)
// stay hand-written so the column-major / rotation conventions are preserved bit-for-bit.
public readonly struct Quat : IEquatable<Quat>
{
    internal readonly Quaternion Q;

    public Quat(float x, float y, float z, float w)
    {
        Q = new Quaternion(
            x: x,
            y: y,
            z: z,
            w: w
        );
    }

    internal Quat(Quaternion q) => Q = q;

    public float X => Q.X;
    public float Y => Q.Y;
    public float Z => Q.Z;
    public float W => Q.W;

    public static readonly Quat Identity = new(
        x: 0,
        y: 0,
        z: 0,
        w: 1
    );

    // ── Constructors ─────────────────────────────────────────────────────────

    public static Quat FromAxisAngle(Vec3 axis, float angleRadians)
    {
        float half = angleRadians * 0.5f;
        float s = MathF.Sin(half);
        var n = axis.Normalize();
        return new Quat(
            x: n.X * s,
            y: n.Y * s,
            z: n.Z * s,
            w: MathF.Cos(half)
        );
    }

    public static Quat FromEuler(float pitch, float yaw, float roll)
    {
        var qp = FromAxisAngle(axis: new Vec3(x: 1, y: 0, z: 0), angleRadians: pitch);
        var qy = FromAxisAngle(axis: new Vec3(x: 0, y: 1, z: 0), angleRadians: yaw);
        var qr = FromAxisAngle(axis: new Vec3(x: 0, y: 0, z: 1), angleRadians: roll);
        return qy * qp * qr;
    }

    public static Quat FromMat4(Mat4 m)
    {
        float trace = m.Get(col: 0, row: 0) + m.Get(col: 1, row: 1) + m.Get(col: 2, row: 2);
        if (trace > 0f)
        {
            float s = 0.5f / MathF.Sqrt(trace + 1f);
            return new Quat(
                x: (m.Get(col: 1, row: 2) - m.Get(col: 2, row: 1)) * s,
                y: (m.Get(col: 2, row: 0) - m.Get(col: 0, row: 2)) * s,
                z: (m.Get(col: 0, row: 1) - m.Get(col: 1, row: 0)) * s,
                w: 0.25f / s
            );
        }

        if (m.Get(col: 0, row: 0) > m.Get(col: 1, row: 1) &&
            m.Get(col: 0, row: 0) > m.Get(col: 2, row: 2))
        {
            float s = 2f * MathF.Sqrt(
                1f + m.Get(col: 0, row: 0) - m.Get(col: 1, row: 1) - m.Get(col: 2, row: 2)
            );
            return new Quat(
                x: 0.25f * s,
                y: (m.Get(col: 1, row: 0) + m.Get(col: 0, row: 1)) / s,
                z: (m.Get(col: 2, row: 0) + m.Get(col: 0, row: 2)) / s,
                w: (m.Get(col: 1, row: 2) - m.Get(col: 2, row: 1)) / s
            );
        }

        if (m.Get(col: 1, row: 1) > m.Get(col: 2, row: 2))
        {
            float s = 2f * MathF.Sqrt(
                1f + m.Get(col: 1, row: 1) - m.Get(col: 0, row: 0) - m.Get(col: 2, row: 2)
            );
            return new Quat(
                x: (m.Get(col: 1, row: 0) + m.Get(col: 0, row: 1)) / s,
                y: 0.25f * s,
                z: (m.Get(col: 2, row: 1) + m.Get(col: 1, row: 2)) / s,
                w: (m.Get(col: 2, row: 0) - m.Get(col: 0, row: 2)) / s
            );
        }
        else
        {
            float s = 2f * MathF.Sqrt(
                1f + m.Get(col: 2, row: 2) - m.Get(col: 0, row: 0) - m.Get(col: 1, row: 1)
            );
            return new Quat(
                x: (m.Get(col: 2, row: 0) + m.Get(col: 0, row: 2)) / s,
                y: (m.Get(col: 2, row: 1) + m.Get(col: 1, row: 2)) / s,
                z: 0.25f * s,
                w: (m.Get(col: 0, row: 1) - m.Get(col: 1, row: 0)) / s
            );
        }
    }

    // ── Operations ───────────────────────────────────────────────────────────

    // System.Numerics.Quaternion's product is term-for-term identical to the hand-written Hamilton
    // product this previously used (verified), so the convention is unchanged — now SIMD.
    public static Quat operator *(Quat a, Quat b) => new(a.Q * b.Q);

    public Quat Normalize()
    {
        float len = MathF.Sqrt((X * X) + (Y * Y) + (Z * Z) + (W * W));
        if (len < float.Epsilon) return Identity;
        float inv = 1f / len;
        return new Quat(
            x: X * inv,
            y: Y * inv,
            z: Z * inv,
            w: W * inv
        );
    }

    public Quat Conjugate() => new(Quaternion.Conjugate(Q));

    public Quat Inverse() => Conjugate().Normalize();

    // Vector3.Transform(v, q) is the standard active rotation of v by q — identical to the
    // uv/uuv cross-product form this previously used, now SIMD.
    public Vec3 RotateVec(Vec3 v) => new(Vector3.Transform(value: v.V, rotation: Q));

    public static Quat Slerp(Quat a, Quat bIn, float t)
    {
        var b = bIn;
        float dot = (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W);
        if (dot < 0f)
        {
            b = new Quat(
                x: -b.X,
                y: -b.Y,
                z: -b.Z,
                w: -b.W
            );
            dot = -dot;
        }

        if (dot > 0.9995f)
        {
            return new Quat(
                x: a.X + ((b.X - a.X) * t),
                y: a.Y + ((b.Y - a.Y) * t),
                z: a.Z + ((b.Z - a.Z) * t),
                w: a.W + ((b.W - a.W) * t)
            ).Normalize();
        }

        float theta0 = MathF.Acos(dot);
        float theta = theta0 * t;
        float sinTheta = MathF.Sin(theta);
        float sinTheta0 = MathF.Sin(theta0);
        float s0 = MathF.Cos(theta) - (dot * sinTheta / sinTheta0);
        float s1 = sinTheta / sinTheta0;
        return new Quat(
            x: (s0 * a.X) + (s1 * b.X),
            y: (s0 * a.Y) + (s1 * b.Y),
            z: (s0 * a.Z) + (s1 * b.Z),
            w: (s0 * a.W) + (s1 * b.W)
        );
    }

    public Mat4 ToMat4()
    {
        var n = Normalize();
        float xx = n.X * n.X;
        float yy = n.Y * n.Y;
        float zz = n.Z * n.Z;
        float xy = n.X * n.Y;
        float xz = n.X * n.Z;
        float yz = n.Y * n.Z;
        float wx = n.W * n.X;
        float wy = n.W * n.Y;
        float wz = n.W * n.Z;
        return new Mat4(
            c0: new Vec4(
                x: 1 - (2 * (yy + zz)),
                y: 2 * (xy + wz),
                z: 2 * (xz - wy),
                w: 0
            ),
            c1: new Vec4(
                x: 2 * (xy - wz),
                y: 1 - (2 * (xx + zz)),
                z: 2 * (yz + wx),
                w: 0
            ),
            c2: new Vec4(
                x: 2 * (xz + wy),
                y: 2 * (yz - wx),
                z: 1 - (2 * (xx + yy)),
                w: 0
            ),
            c3: new Vec4(
                x: 0,
                y: 0,
                z: 0,
                w: 1
            )
        );
    }

    /// <summary>Euler angles in radians (pitch, yaw, roll) for Zig FFI sync.</summary>
    public Vec3 ToEulerRadians()
    {
        float pitch = MathF.Atan2(y: 2f * ((W * X) + (Y * Z)), x: 1f - (2f * ((X * X) + (Y * Y))));
        float yaw = MathF.Asin(Math.Clamp(value: 2f * ((W * Y) - (Z * X)), min: -1f, max: 1f));
        float roll = MathF.Atan2(y: 2f * ((W * Z) + (X * Y)), x: 1f - (2f * ((Y * Y) + (Z * Z))));
        return new Vec3(x: pitch, y: yaw, z: roll);
    }

    public float[] ToArray() => [X, Y, Z, W];

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(Quat other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;

    public override bool Equals(object? obj) => obj is Quat q && Equals(q);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            value1: X,
            value2: Y,
            value3: Z,
            value4: W
        );
    }

    public override string ToString() => $"Quat({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";

    public static bool operator ==(Quat a, Quat b) => a.Equals(b);

    public static bool operator !=(Quat a, Quat b) => !a.Equals(b);
}
