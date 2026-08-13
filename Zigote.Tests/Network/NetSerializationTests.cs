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
        w.WriteBits(0b101, 3);
        w.WriteBits(0xABCD, 16);
        w.WriteBool(true);

        var r = RoundTrip(w);
        Assert.True(r.ReadBool());
        Assert.False(r.ReadBool());
        Assert.Equal(0b101u, r.ReadBits(3));
        Assert.Equal(0xABCDu, r.ReadBits(16));
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
        Assert.Equal(value, RoundTrip(w).ReadVarUInt());
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
        Assert.Equal(value, RoundTrip(w).ReadVarInt());
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
        Assert.Equal((byte)200, r.ReadByte());
        Assert.Equal((ushort)40000, r.ReadUInt16());
        Assert.Equal(-123456, r.ReadInt32());
        Assert.Equal(0xDEAD_BEEF_F00D_1234, r.ReadUInt64());
        Assert.Equal(3.14159f, r.ReadSingle(), 5);
        Assert.Equal(2.718281828, r.ReadDouble(), 9);
        Assert.Equal("héllo 🌍", r.ReadString());
    }

    [Fact]
    public void RangedSingle_Quantizes_Within_Tolerance()
    {
        var w = new NetWriter();
        w.WriteRangedSingle(
            0.5f,
            0f,
            1f,
            16
        );
        w.WriteRangedSingle(
            -50f,
            -100f,
            100f,
            16
        );
        w.WriteRangedSingle(
            1000f,
            0f,
            1f,
            8
        ); // clamped to max

        var r = RoundTrip(w);
        Assert.Equal(0.5f, r.ReadRangedSingle(0f, 1f, 16), 3);
        Assert.Equal(-50f, r.ReadRangedSingle(-100f, 100f, 16), 1);
        Assert.Equal(1f, r.ReadRangedSingle(0f, 1f, 8), 2);
    }

    [Fact]
    public void Vectors_And_Color_RoundTrip()
    {
        var w = new NetWriter();
        w.WriteVec3(new Vec3(1, -2, 3.5f));
        w.WriteVec2(new Vec2(9, 8));
        w.WriteColor(new Color(0.25f, 0.5f, 0.75f));

        var r = RoundTrip(w);
        var v3 = r.ReadVec3();
        Assert.Equal(1f, v3.X, 4);
        Assert.Equal(-2f, v3.Y, 4);
        Assert.Equal(3.5f, v3.Z, 4);
        var v2 = r.ReadVec2();
        Assert.Equal(9f, v2.X, 4);
        var c = r.ReadColor();
        Assert.Equal(0.5f, c.G, 2);
        Assert.Equal(1f, c.A, 2);
    }

    [Fact]
    public void Quaternion_SmallestThree_RoundTrips_Close()
    {
        foreach (var q in new[] {
                     Quat.Identity,
                     Quat.FromAxisAngle(new Vec3(0, 1, 0), 1.2f),
                     Quat.FromAxisAngle(new Vec3(1, 0, 0), -2.0f),
                     Quat.FromEuler(0.5f, -1.3f, 0.9f),
                 })
        {
            var w = new NetWriter();
            w.WriteQuaternion(q, 12);
            var decoded = RoundTrip(w).ReadQuaternion(12);

            // q and -q are the same rotation; compare via |dot| near 1.
            var dot = MathF.Abs(
                q.X * decoded.X + q.Y * decoded.Y + q.Z * decoded.Z + q.W * decoded.W
            );
            Assert.True(dot > 0.999f, $"quat drift too high: dot={dot}");
        }
    }

    [Fact]
    public void Transform_RoundTrips()
    {
        var t = new Transform3D(
            new Vec3(10, 20, 30),
            Quat.FromAxisAngle(new Vec3(0, 1, 0), 0.7f),
            new Vec3(2, 2, 2)
        );
        var w = new NetWriter();
        w.WriteTransform(t);
        var d = RoundTrip(w).ReadTransform();

        Assert.Equal(10f, d.Position.X, 3);
        Assert.Equal(2f, d.Scale.Y, 3);
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
        Assert.Equal(2, w.ByteLength);
        Assert.Equal((ushort)7, RoundTrip(w).ReadUInt16());
    }
}
