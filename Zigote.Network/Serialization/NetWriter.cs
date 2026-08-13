using System.Runtime.CompilerServices;
using System.Text;

namespace Zigote.Network;

/// <summary>
///     Bit-level serializer. Packs values into a growable byte buffer with no padding between fields,
///     so a
///     bool costs one bit and a quantized float costs exactly the bits you ask for. Pair with
///     <see cref="NetReader" />, reading fields back in the same order. Not thread-safe; reuse one per
///     send-thread and <see cref="Clear" /> between messages to avoid allocation.
/// </summary>
public sealed class NetWriter
{
    private byte[] _data;
    private int _length; // whole bytes already flushed
    private ulong _scratch; // pending bits, LSB-first (always &lt; 8 valid bits after a write)
    private int _scratchBits;

    public NetWriter(int initialCapacityBytes = 256)
    {
        _data = new byte[Math.Max(16, initialCapacityBytes)];
    }

    /// <summary>Total bits written so far.</summary>
    public int BitLength => _length * 8 + _scratchBits;

    /// <summary>Bytes needed to hold everything written (rounds the final partial byte up).</summary>
    public int ByteLength => (BitLength + 7) >> 3;

    /// <summary>Reset to empty for reuse. Keeps the backing buffer.</summary>
    public void Clear()
    {
        _length = 0;
        _scratch = 0;
        _scratchBits = 0;
    }

    /// <summary>Write the low <paramref name="bits" /> bits of <paramref name="value" /> (1..32).</summary>
    public void WriteBits(uint value, int bits)
    {
        if (bits <= 0) return;
        if (bits < 32) value &= (1u << bits) - 1;

        _scratch |= (ulong)value << _scratchBits;
        _scratchBits += bits;

        while (_scratchBits >= 8)
        {
            EnsureByte();
            _data[_length++] = (byte)(_scratch & 0xFF);
            _scratch >>= 8;
            _scratchBits -= 8;
        }
    }

    public void WriteBool(bool value)
    {
        WriteBits(value ? 1u : 0u, 1);
    }

    public void WriteByte(byte value)
    {
        WriteBits(value, 8);
    }

    public void WriteUInt16(ushort value)
    {
        WriteBits(value, 16);
    }

    public void WriteInt16(short value)
    {
        WriteBits((ushort)value, 16);
    }

    public void WriteUInt32(uint value)
    {
        WriteBits(value, 32);
    }

    public void WriteInt32(int value)
    {
        WriteBits((uint)value, 32);
    }

    public void WriteUInt64(ulong value)
    {
        WriteBits((uint)(value & 0xFFFF_FFFF), 32);
        WriteBits((uint)(value >> 32), 32);
    }

    public void WriteInt64(long value)
    {
        WriteUInt64((ulong)value);
    }

    public void WriteSingle(float value)
    {
        WriteBits(BitConverter.SingleToUInt32Bits(value), 32);
    }

    public void WriteHalf(Half value)
    {
        WriteBits(BitConverter.HalfToUInt16Bits(value), 16);
    }

    public void WriteDouble(double value)
    {
        WriteUInt64(BitConverter.DoubleToUInt64Bits(value));
    }

    /// <summary>
    ///     Variable-length unsigned int: 7 payload bits per group + 1 continuation bit. 1 byte for
    ///     small values.
    /// </summary>
    public void WriteVarUInt(uint value)
    {
        while (value >= 0x80)
        {
            WriteBits((value & 0x7F) | 0x80, 8);
            value >>= 7;
        }

        WriteBits(value, 8);
    }

    public void WriteVarULong(ulong value)
    {
        while (value >= 0x80)
        {
            WriteBits((uint)((value & 0x7F) | 0x80), 8);
            value >>= 7;
        }

        WriteBits((uint)value, 8);
    }

    /// <summary>
    ///     ZigZag-encoded variable-length signed int (small magnitudes — positive or negative — are 1
    ///     byte).
    /// </summary>
    public void WriteVarInt(int value)
    {
        WriteVarUInt((uint)((value << 1) ^ (value >> 31)));
    }

    /// <summary>
    ///     Quantize <paramref name="value" /> from [<paramref name="min" />, <paramref name="max" />] into
    ///     <paramref name="bits" /> bits (1..31). Out-of-range values are clamped. Lossy but compact — the
    ///     workhorse for positions, angles and normalized scalars.
    /// </summary>
    public void WriteRangedSingle(float value, float min, float max, int bits)
    {
        if (max <= min || bits <= 0)
        {
            WriteSingle(value);
            return;
        }

        var levels = bits >= 31 ? uint.MaxValue >> 1 : (1u << bits) - 1;
        var t = Math.Clamp((value - min) / (max - min), 0f, 1f);
        var quantized = (uint)MathF.Round(t * levels);
        WriteBits(quantized, bits);
    }

    /// <summary>Write a raw byte span (length-prefixed with a var-uint).</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        WriteVarUInt((uint)bytes.Length);
        for (var i = 0; i < bytes.Length; i++) WriteBits(bytes[i], 8);
    }

    /// <summary>Write a UTF-8 string (length-prefixed). Null is written as a zero-length string.</summary>
    public void WriteString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteVarUInt(0);
            return;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        var buffer = count <= 256 ? stackalloc byte[count] : new byte[count];
        Encoding.UTF8.GetBytes(value, buffer);
        WriteBytes(buffer);
    }

    public void Write<T>(in T value) where T : INetSerializable
    {
        value.Serialize(this);
    }

    /// <summary>
    ///     The bytes written so far, including the final partial byte (zero-padded). Valid until the
    ///     next write.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan()
    {
        if (_scratchBits > 0)
        {
            EnsureByte();
            _data[_length] =
                (byte)(_scratch & 0xFF); // materialize the partial byte without advancing _length
        }

        return _data.AsSpan(0, ByteLength);
    }

    public byte[] ToArray()
    {
        return AsSpan().ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureByte()
    {
        if (_length + 1 > _data.Length) Grow(_length + 1);
    }

    private void Grow(int needed)
    {
        var size = _data.Length * 2;
        while (size < needed) size *= 2;
        Array.Resize(ref _data, size);
    }
}
