using System.Text;

namespace Zigote.Network;

/// <summary>
///     Reads back values packed by <see cref="NetWriter" />, in the same order they were written.
///     Reading
///     past the end yields zeros and trips <see cref="Overflow" /> rather than throwing, so a
///     malformed or
///     truncated packet degrades safely instead of crashing the receive loop.
/// </summary>
public sealed class NetReader
{
    private int _byteLength;
    private byte[] _data;
    private int _readPos; // next whole byte to pull into scratch
    private ulong _scratch;
    private int _scratchBits;

    public NetReader() => _data = [];

    public NetReader(ReadOnlySpan<byte> data)
    {
        _data = [];
        SetSource(data);
    }

    /// <summary>True once a read ran past the available data — the rest of the message is untrustworthy.</summary>
    public bool Overflow { get; private set; }

    /// <summary>Bits consumed so far.</summary>
    public int BitPosition => (_readPos * 8) - _scratchBits;

    /// <summary>Bits still available to read.</summary>
    public int BitsRemaining => (_byteLength * 8) - BitPosition;

    /// <summary>Point the reader at a new payload, reusing this instance (no allocation).</summary>
    public void SetSource(ReadOnlySpan<byte> data)
    {
        if (_data.Length < data.Length) _data = new byte[Math.Max(val1: 16, val2: data.Length)];
        data.CopyTo(_data);
        _byteLength = data.Length;
        _readPos = 0;
        _scratch = 0;
        _scratchBits = 0;
        Overflow = false;
    }

    public uint ReadBits(int bits)
    {
        if (bits <= 0) return 0;

        while (_scratchBits < bits)
        {
            byte b = 0;
            if (_readPos < _byteLength) b = _data[_readPos];
            else Overflow = true;
            _readPos++;
            _scratch |= (ulong)b << _scratchBits;
            _scratchBits += 8;
        }

        uint mask = bits >= 32 ? uint.MaxValue : (1u << bits) - 1;
        uint result = (uint)(_scratch & mask);
        _scratch >>= bits;
        _scratchBits -= bits;
        return result;
    }

    public bool ReadBool() => ReadBits(1) != 0;

    public byte ReadByte() => (byte)ReadBits(8);

    public ushort ReadUInt16() => (ushort)ReadBits(16);

    public short ReadInt16() => (short)ReadBits(16);

    public uint ReadUInt32() => ReadBits(32);

    public int ReadInt32() => (int)ReadBits(32);

    public ulong ReadUInt64()
    {
        ulong lo = ReadBits(32);
        ulong hi = ReadBits(32);
        return lo | (hi << 32);
    }

    public long ReadInt64() => (long)ReadUInt64();

    public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadBits(32));

    public Half ReadHalf() => BitConverter.UInt16BitsToHalf((ushort)ReadBits(16));

    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadUInt64());

    public uint ReadVarUInt()
    {
        uint result = 0;
        int shift = 0;
        while (shift < 35)
        {
            uint b = ReadBits(8);
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }

        return result;
    }

    public ulong ReadVarULong()
    {
        ulong result = 0;
        int shift = 0;
        while (shift < 70)
        {
            ulong b = ReadBits(8);
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }

        return result;
    }

    public int ReadVarInt()
    {
        uint raw = ReadVarUInt();
        return (int)(raw >> 1) ^ -(int)(raw & 1);
    }

    public float ReadRangedSingle(float min, float max, int bits)
    {
        if (max <= min || bits <= 0) return ReadSingle();

        uint levels = bits >= 31 ? uint.MaxValue >> 1 : (1u << bits) - 1;
        uint quantized = ReadBits(bits);
        float t = levels == 0 ? 0f : (float)quantized / levels;
        return min + (t * (max - min));
    }

    /// <summary>Read a length-prefixed byte blob. Returns an empty array on overflow.</summary>
    public byte[] ReadBytes()
    {
        uint count = ReadVarUInt();
        if (count == 0 || count > (BitsRemaining / 8) + 1) // guard against a bogus huge length
        {
            if (count != 0) Overflow = true;
            return [];
        }

        byte[] result = new byte[count];
        for (int i = 0; i < count; i++) result[i] = (byte)ReadBits(8);
        return result;
    }

    public string ReadString()
    {
        byte[] bytes = ReadBytes();
        return bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    public T Read<T>() where T : INetSerializable, new()
    {
        var value = new T();
        value.Deserialize(this);
        return value;
    }
}
