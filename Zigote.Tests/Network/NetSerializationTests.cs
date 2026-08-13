using Xunit;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Network;

namespace Zigote.Tests;

public class NetSerializationTests
{
    private static NetReader RoundTrip(NetWriter w)
    {
        var reader = new NetReader();
        reader.SetSource(w.AsSpan());
        return reader;
    }

    [Fact]
    public void Bits_And_Bools_RoundTrip_Bit_Exact()
    {
        var w = new NetWriter();
        w.WriteBool(true);
        w.WriteBool(false);
        w.WriteBits(value: 0b101, bits: 3);
        w.WriteBits(value: 0xABCD, bits: 16);
        w.WriteBool(true);

        var r = RoundTrip(w);
        Assert.True(r.ReadBool());
        Assert.False(r.ReadBool());
        Assert.Equal(expected: 0b101u, actual: r.ReadBits(3));
        Assert.Equal(expected: 0xABCDu, actual: r.ReadBits(16));
        Assert.True(r.ReadBool());
        Assert.False(r.Overflow);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(300u)]
    [InlineData(70000u)]
    [InlineData(uint.MaxValue)]
    public void VarUInt_RoundTrips(uint value)
    {
        var w = new NetWriter();
        w.WriteVarUInt(value);
        Assert.Equal(expected: value, actual: RoundTrip(w).ReadVarUInt());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(-1000)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void VarInt_RoundTrips(int value)
    {
        var w = new NetWriter();
        w.WriteVarInt(value);
        Assert.Equal(expected: value, actual: RoundTrip(w).ReadVarInt());
    }

    [Fact]
    public void Primitives_RoundTrip()
    {
        var w = new NetWriter();
        w.WriteByte(200);
        w.WriteUInt16(40000);
        w.WriteInt32(-123456);
        w.WriteUInt64(0xDEAD_BEEF_F00D_1234);
        w.WriteSingle(3.14159f);
        w.WriteDouble(2.718281828);
        w.WriteString("héllo 🌍");

        var r = RoundTrip(w);
        Assert.Equal(expected: (byte)200, actual: r.ReadByte());
        Assert.Equal(expected: (ushort)40000, actual: r.ReadUInt16());
        Assert.Equal(expected: -123456, actual: r.ReadInt32());
        Assert.Equal(expected: 0xDEAD_BEEF_F00D_1234, actual: r.ReadUInt64());
        Assert.Equal(expected: 3.14159f, actual: r.ReadSingle(), precision: 5);
        Assert.Equal(expected: 2.718281828, actual: r.ReadDouble(), precision: 9);
        Assert.Equal(expected: "héllo 🌍", actual: r.ReadString());
    }

    [Fact]
    public void RangedSingle_Quantizes_Within_Tolerance()
    {
        var w = new NetWriter();
        w.WriteRangedSingle(
            value: 0.5f,
            min: 0f,
            max: 1f,
            bits: 16
        );
        w.WriteRangedSingle(
            value: -50f,
            min: -100f,
            max: 100f,
            bits: 16
        );
        w.WriteRangedSingle(
            value: 1000f,
            min: 0f,
            max: 1f,
            bits: 8
        ); // clamped to max

        var r = RoundTrip(w);
        Assert.Equal(
            expected: 0.5f,
            actual: r.ReadRangedSingle(min: 0f, max: 1f, bits: 16),
            precision: 3
        );
        Assert.Equal(
            expected: -50f,
            actual: r.ReadRangedSingle(min: -100f, max: 100f, bits: 16),
            precision: 1
        );
        Assert.Equal(
            expected: 1f,
            actual: r.ReadRangedSingle(min: 0f, max: 1f, bits: 8),
            precision: 2
        );
    }

    [Fact]
    public void Vectors_And_Color_RoundTrip()
    {
        var w = new NetWriter();
        w.WriteVec3(new Vec3(x: 1, y: -2, z: 3.5f));
        w.WriteVec2(new Vec2(x: 9, y: 8));
        w.WriteColor(new Color(r: 0.25f, g: 0.5f, b: 0.75f));

        var r = RoundTrip(w);
        var v3 = r.ReadVec3();
        Assert.Equal(expected: 1f, actual: v3.X, precision: 4);
        Assert.Equal(expected: -2f, actual: v3.Y, precision: 4);
        Assert.Equal(expected: 3.5f, actual: v3.Z, precision: 4);
        var v2 = r.ReadVec2();
        Assert.Equal(expected: 9f, actual: v2.X, precision: 4);
        var c = r.ReadColor();
        Assert.Equal(expected: 0.5f, actual: c.G, precision: 2);
        Assert.Equal(expected: 1f, actual: c.A, precision: 2);
    }

    [Fact]
    public void Quaternion_SmallestThree_RoundTrips_Close()
    {
        foreach (var q in new[] {
                     Quat.Identity,
                     Quat.FromAxisAngle(axis: new Vec3(x: 0, y: 1, z: 0), angleRadians: 1.2f),
                     Quat.FromAxisAngle(axis: new Vec3(x: 1, y: 0, z: 0), angleRadians: -2.0f),
                     Quat.FromEuler(pitch: 0.5f, yaw: -1.3f, roll: 0.9f),
                 })
        {
            var w = new NetWriter();
            w.WriteQuaternion(q: q, bitsPerComponent: 12);
            var decoded = RoundTrip(w).ReadQuaternion(12);

            // q and -q are the same rotation; compare via |dot| near 1.
            float dot = MathF.Abs(
                (q.X * decoded.X) + (q.Y * decoded.Y) + (q.Z * decoded.Z) + (q.W * decoded.W)
            );
            Assert.True(condition: dot > 0.999f, userMessage: $"quat drift too high: dot={dot}");
        }
    }

    [Fact]
    public void Transform_RoundTrips()
    {
        var t = new Transform3D(
            position: new Vec3(x: 10, y: 20, z: 30),
            rotation: Quat.FromAxisAngle(axis: new Vec3(x: 0, y: 1, z: 0), angleRadians: 0.7f),
            scale: new Vec3(x: 2, y: 2, z: 2)
        );
        var w = new NetWriter();
        w.WriteTransform(t);
        var d = RoundTrip(w).ReadTransform();

        Assert.Equal(expected: 10f, actual: d.Position.X, precision: 3);
        Assert.Equal(expected: 2f, actual: d.Scale.Y, precision: 3);
    }

    [Fact]
    public void Reader_Past_End_Sets_Overflow_Not_Throws()
    {
        var w = new NetWriter();
        w.WriteByte(1);
        var r = RoundTrip(w);
        r.ReadByte();
        r.ReadUInt64(); // past end
        Assert.True(r.Overflow);
    }

    [Fact]
    public void Clear_Allows_Writer_Reuse()
    {
        var w = new NetWriter();
        w.WriteUInt32(42);
        w.Clear();
        w.WriteUInt16(7);
        Assert.Equal(expected: 2, actual: w.ByteLength);
        Assert.Equal(expected: (ushort)7, actual: RoundTrip(w).ReadUInt16());
    }
}
