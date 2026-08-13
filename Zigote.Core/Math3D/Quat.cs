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
            x,
            y,
            z,
            w
        );
    }

    internal Quat(Quaternion q)
    {
        Q = q;
    }

    public float X => Q.X;
    public float Y => Q.Y;
    public float Z => Q.Z;
    public float W => Q.W;

    public static readonly Quat Identity = new(
        0,
        0,
        0,
        1
    );

    // ── Constructors ─────────────────────────────────────────────────────────

    public static Quat FromAxisAngle(Vec3 axis, float angleRadians)
    {
        var half = angleRadians * 0.5f;
        var s = MathF.Sin(half);
        var n = axis.Normalize();
        return new Quat(
            n.X * s,
            n.Y * s,
            n.Z * s,
            MathF.Cos(half)
        );
    }

    public static Quat FromEuler(float pitch, float yaw, float roll)
    {
        var qp = FromAxisAngle(new Vec3(1, 0, 0), pitch);
        var qy = FromAxisAngle(new Vec3(0, 1, 0), yaw);
        var qr = FromAxisAngle(new Vec3(0, 0, 1), roll);
        return qy * qp * qr;
    }

    public static Quat FromMat4(Mat4 m)
    {
        var trace = m.Get(0, 0) + m.Get(1, 1) + m.Get(2, 2);
        if (trace > 0f)
        {
            var s = 0.5f / MathF.Sqrt(trace + 1f);
            return new Quat(
                (m.Get(1, 2) - m.Get(2, 1)) * s,
                (m.Get(2, 0) - m.Get(0, 2)) * s,
                (m.Get(0, 1) - m.Get(1, 0)) * s,
                0.25f / s
            );
        }

        if (m.Get(0, 0) > m.Get(1, 1) && m.Get(0, 0) > m.Get(2, 2))
        {
            var s = 2f * MathF.Sqrt(1f + m.Get(0, 0) - m.Get(1, 1) - m.Get(2, 2));
            return new Quat(
                0.25f * s,
                (m.Get(1, 0) + m.Get(0, 1)) / s,
                (m.Get(2, 0) + m.Get(0, 2)) / s,
                (m.Get(1, 2) - m.Get(2, 1)) / s
            );
        }

        if (m.Get(1, 1) > m.Get(2, 2))
        {
            var s = 2f * MathF.Sqrt(1f + m.Get(1, 1) - m.Get(0, 0) - m.Get(2, 2));
            return new Quat(
                (m.Get(1, 0) + m.Get(0, 1)) / s,
                0.25f * s,
                (m.Get(2, 1) + m.Get(1, 2)) / s,
                (m.Get(2, 0) - m.Get(0, 2)) / s
            );
        }
        else
        {
            var s = 2f * MathF.Sqrt(1f + m.Get(2, 2) - m.Get(0, 0) - m.Get(1, 1));
            return new Quat(
                (m.Get(2, 0) + m.Get(0, 2)) / s,
                (m.Get(2, 1) + m.Get(1, 2)) / s,
                0.25f * s,
                (m.Get(0, 1) - m.Get(1, 0)) / s
            );
        }
    }

    // ── Operations ───────────────────────────────────────────────────────────

    // System.Numerics.Quaternion's product is term-for-term identical to the hand-written Hamilton
    // product this previously used (verified), so the convention is unchanged — now SIMD.
    public static Quat operator *(Quat a, Quat b)
    {
        return new Quat(a.Q * b.Q);
    }

    public Quat Normalize()
    {
        var len = MathF.Sqrt(X * X + Y * Y + Z * Z + W * W);
        if (len < float.Epsilon) return Identity;
        var inv = 1f / len;
        return new Quat(
            X * inv,
            Y * inv,
            Z * inv,
            W * inv
        );
    }

    public Quat Conjugate()
    {
        return new Quat(Quaternion.Conjugate(Q));
    }

    public Quat Inverse()
    {
        return Conjugate().Normalize();
    }

    // Vector3.Transform(v, q) is the standard active rotation of v by q — identical to the
    // uv/uuv cross-product form this previously used, now SIMD.
    public Vec3 RotateVec(Vec3 v)
    {
        return new Vec3(Vector3.Transform(v.V, Q));
    }

    public static Quat Slerp(Quat a, Quat bIn, float t)
    {
        var b = bIn;
        var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (dot < 0f)
        {
            b = new Quat(
                -b.X,
                -b.Y,
                -b.Z,
                -b.W
            );
            dot = -dot;
        }

        if (dot > 0.9995f)
            return new Quat(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t,
                a.W + (b.W - a.W) * t
            ).Normalize();

        var theta0 = MathF.Acos(dot);
        var theta = theta0 * t;
        var sinTheta = MathF.Sin(theta);
        var sinTheta0 = MathF.Sin(theta0);
        var s0 = MathF.Cos(theta) - dot * sinTheta / sinTheta0;
        var s1 = sinTheta / sinTheta0;
        return new Quat(
            s0 * a.X + s1 * b.X,
            s0 * a.Y + s1 * b.Y,
            s0 * a.Z + s1 * b.Z,
            s0 * a.W + s1 * b.W
        );
    }

    public Mat4 ToMat4()
    {
        var n = Normalize();
        var xx = n.X * n.X;
        var yy = n.Y * n.Y;
        var zz = n.Z * n.Z;
        var xy = n.X * n.Y;
        var xz = n.X * n.Z;
        var yz = n.Y * n.Z;
        var wx = n.W * n.X;
        var wy = n.W * n.Y;
        var wz = n.W * n.Z;
        return new Mat4(
            new Vec4(
                1 - 2 * (yy + zz),
                2 * (xy + wz),
                2 * (xz - wy),
                0
            ),
            new Vec4(
                2 * (xy - wz),
                1 - 2 * (xx + zz),
                2 * (yz + wx),
                0
            ),
            new Vec4(
                2 * (xz + wy),
                2 * (yz - wx),
                1 - 2 * (xx + yy),
                0
            ),
            new Vec4(
                0,
                0,
                0,
                1
            )
        );
    }

    /// <summary>Euler angles in radians (pitch, yaw, roll) for Zig FFI sync.</summary>
    public Vec3 ToEulerRadians()
    {
        var pitch = MathF.Atan2(2f * (W * X + Y * Z), 1f - 2f * (X * X + Y * Y));
        var yaw = MathF.Asin(Math.Clamp(2f * (W * Y - Z * X), -1f, 1f));
        var roll = MathF.Atan2(2f * (W * Z + X * Y), 1f - 2f * (Y * Y + Z * Z));
        return new Vec3(pitch, yaw, roll);
    }

    public float[] ToArray()
    {
        return [X, Y, Z, W];
    }

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(Quat other)
    {
        return X == other.X && Y == other.Y && Z == other.Z && W == other.W;
    }

    public override bool Equals(object? obj)
    {
        return obj is Quat q && Equals(q);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            X,
            Y,
            Z,
            W
        );
    }

    public override string ToString()
    {
        return $"Quat({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";
    }

    public static bool operator ==(Quat a, Quat b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Quat a, Quat b)
    {
        return !a.Equals(b);
    }
}
