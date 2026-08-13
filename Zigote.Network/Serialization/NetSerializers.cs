using Zigote.Core;
using Zigote.Core.Math3D;

namespace Zigote.Network;

/// <summary>
///     Bit-serializer extensions for the engine's value types. Each comes in a full-precision form and
///     a
///     compact quantized form so game code chooses the bandwidth/accuracy trade-off per field.
///     Quaternions
///     use smallest-three compression (drop the largest component, reconstruct it from unit length).
/// </summary>
public static class NetSerializers
{
    private const float
        QuatComponentRange =
            0.7071067811865476f; // 1 / sqrt(2): bound on the three smaller components

    // ── Vec2 ───────────────────────────────────────────────────────────────
    public static void WriteVec2(this NetWriter w, Vec2 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
    }

    public static Vec2 ReadVec2(this NetReader r)
    {
        return new Vec2(r.ReadSingle(), r.ReadSingle());
    }

    public static void WriteRangedVec2(this NetWriter w, Vec2 v, float min, float max, int bits)
    {
        w.WriteRangedSingle(
            v.X,
            min,
            max,
            bits
        );
        w.WriteRangedSingle(
            v.Y,
            min,
            max,
            bits
        );
    }

    public static Vec2 ReadRangedVec2(this NetReader r, float min, float max, int bits)
    {
        return new Vec2(r.ReadRangedSingle(min, max, bits), r.ReadRangedSingle(min, max, bits));
    }

    // ── Vec3 ───────────────────────────────────────────────────────────────
    public static void WriteVec3(this NetWriter w, Vec3 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
        w.WriteSingle(v.Z);
    }

    public static Vec3 ReadVec3(this NetReader r)
    {
        return new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    public static void WriteRangedVec3(this NetWriter w, Vec3 v, float min, float max, int bits)
    {
        w.WriteRangedSingle(
            v.X,
            min,
            max,
            bits
        );
        w.WriteRangedSingle(
            v.Y,
            min,
            max,
            bits
        );
        w.WriteRangedSingle(
            v.Z,
            min,
            max,
            bits
        );
    }

    public static Vec3 ReadRangedVec3(this NetReader r, float min, float max, int bits)
    {
        return new Vec3(
            r.ReadRangedSingle(min, max, bits),
            r.ReadRangedSingle(min, max, bits),
            r.ReadRangedSingle(min, max, bits)
        );
    }

    // ── Vec4 ───────────────────────────────────────────────────────────────
    public static void WriteVec4(this NetWriter w, Vec4 v)
    {
        w.WriteSingle(v.X);
        w.WriteSingle(v.Y);
        w.WriteSingle(v.Z);
        w.WriteSingle(v.W);
    }

    public static Vec4 ReadVec4(this NetReader r)
    {
        return new Vec4(
            r.ReadSingle(),
            r.ReadSingle(),
            r.ReadSingle(),
            r.ReadSingle()
        );
    }

    // ── Quaternion (smallest-three) ──────────────────────────────────────────
    public static void WriteQuaternion(this NetWriter w, Quat q, int bitsPerComponent = 10)
    {
        // Normalize defensively; q and -q are the same rotation so we can force the dropped component positive.
        var len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (len < 1e-8f)
        {
            q = Quat.Identity;
            len = 1f;
        }

        float x = q.X / len, y = q.Y / len, z = q.Z / len, wc = q.W / len;

        var largest = 0;
        var maxAbs = MathF.Abs(x);
        if (MathF.Abs(y) > maxAbs)
        {
            largest = 1;
            maxAbs = MathF.Abs(y);
        }

        if (MathF.Abs(z) > maxAbs)
        {
            largest = 2;
            maxAbs = MathF.Abs(z);
        }

        if (MathF.Abs(wc) > maxAbs) largest = 3;

        var sign = largest switch {
            0 => x < 0 ? -1f : 1f,
            1 => y < 0 ? -1f : 1f,
            2 => z < 0 ? -1f : 1f,
            _ => wc < 0 ? -1f : 1f,
        };

        w.WriteBits((uint)largest, 2);
        if (largest != 0)
            w.WriteRangedSingle(
                x * sign,
                -QuatComponentRange,
                QuatComponentRange,
                bitsPerComponent
            );
        if (largest != 1)
            w.WriteRangedSingle(
                y * sign,
                -QuatComponentRange,
                QuatComponentRange,
                bitsPerComponent
            );
        if (largest != 2)
            w.WriteRangedSingle(
                z * sign,
                -QuatComponentRange,
                QuatComponentRange,
                bitsPerComponent
            );
        if (largest != 3)
            w.WriteRangedSingle(
                wc * sign,
                -QuatComponentRange,
                QuatComponentRange,
                bitsPerComponent
            );
    }

    public static Quat ReadQuaternion(this NetReader r, int bitsPerComponent = 10)
    {
        var largest = (int)r.ReadBits(2);
        var a = largest != 0
            ? r.ReadRangedSingle(-QuatComponentRange, QuatComponentRange, bitsPerComponent)
            : 0f;
        var b = largest != 1
            ? r.ReadRangedSingle(-QuatComponentRange, QuatComponentRange, bitsPerComponent)
            : 0f;
        var c = largest != 2
            ? r.ReadRangedSingle(-QuatComponentRange, QuatComponentRange, bitsPerComponent)
            : 0f;
        var d = largest != 3
            ? r.ReadRangedSingle(-QuatComponentRange, QuatComponentRange, bitsPerComponent)
            : 0f;

        // Reconstruct the dropped (largest) component from unit length.
        var sumSq = a * a + b * b + c * c + d * d;
        var dropped = MathF.Sqrt(MathF.Max(0f, 1f - sumSq));

        return largest switch {
            0 => new Quat(
                dropped,
                b,
                c,
                d
            ).Normalize(),
            1 => new Quat(
                a,
                dropped,
                c,
                d
            ).Normalize(),
            2 => new Quat(
                a,
                b,
                dropped,
                d
            ).Normalize(),
            _ => new Quat(
                a,
                b,
                c,
                dropped
            ).Normalize(),
        };
    }

    public static void WriteQuaternionFull(this NetWriter w, Quat q)
    {
        w.WriteSingle(q.X);
        w.WriteSingle(q.Y);
        w.WriteSingle(q.Z);
        w.WriteSingle(q.W);
    }

    public static Quat ReadQuaternionFull(this NetReader r)
    {
        return new Quat(
            r.ReadSingle(),
            r.ReadSingle(),
            r.ReadSingle(),
            r.ReadSingle()
        );
    }

    // ── Color ────────────────────────────────────────────────────────────────
    public static void WriteColor(this NetWriter w, Color c)
    {
        w.WriteByte((byte)Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255));
        w.WriteByte((byte)Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255));
        w.WriteByte((byte)Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255));
        w.WriteByte((byte)Math.Clamp((int)MathF.Round(c.A * 255f), 0, 255));
    }

    public static Color ReadColor(this NetReader r)
    {
        return new Color(
            r.ReadByte() / 255f,
            r.ReadByte() / 255f,
            r.ReadByte() / 255f,
            r.ReadByte() / 255f
        );
    }

    // ── Transform3D ──────────────────────────────────────────────────────────
    public static void WriteTransform(this NetWriter w, Transform3D t)
    {
        w.WriteVec3(t.Position);
        w.WriteQuaternion(t.Rotation);
        w.WriteVec3(t.Scale);
    }

    public static Transform3D ReadTransform(this NetReader r)
    {
        return new Transform3D(r.ReadVec3(), r.ReadQuaternion(), r.ReadVec3());
    }

    // ── NetId ────────────────────────────────────────────────────────────────
    public static void WriteNetId(this NetWriter w, NetId id)
    {
        w.WriteVarUInt(id.Value);
    }

    public static NetId ReadNetId(this NetReader r)
    {
        return new NetId(r.ReadVarUInt());
    }
}
