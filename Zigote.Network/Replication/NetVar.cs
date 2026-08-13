using Zigote.Core;
using Zigote.Core.Math3D;

namespace Zigote.Network;

/// <summary>
///     A replicated field, type-erased so a <see cref="NetObject" /> can hold a heterogeneous
///     list of them.
/// </summary>
public interface INetVar
{
    /// <summary>True while the value has changed recently enough to still be (re)sent (redundancy window).</summary>
    bool IsDirty { get; }

    void Serialize(NetWriter writer);
    void Deserialize(NetReader reader);

    /// <summary>Advance the redundancy window one tick (called once per replication tick by the owner).</summary>
    void Tick();

    void MarkClean();
}

/// <summary>
///     A single replicated value on a <see cref="NetObject" />. Assigning a different value marks it
///     dirty for
///     a small redundancy window (so one dropped unreliable state packet doesn't lose the change), and
///     raises
///     <see cref="Changed" />. The (de)serialize delegates decide the wire encoding — use the
///     <see cref="NetVars" />
///     factories for the common types, or supply your own for quantized/compact fields.
/// </summary>
public sealed class NetVar<T> : INetVar
{
    private readonly Func<NetReader, T> _read;
    private readonly int _redundancy;
    private readonly Action<NetWriter, T> _write;
    private int _dirtyTicks;
    private T _value;

    public NetVar(T initial, Action<NetWriter, T> write, Func<NetReader, T> read,
        int redundancyTicks = 3)
    {
        _value = initial;
        _write = write;
        _read = read;
        _redundancy = Math.Max(val1: 1, val2: redundancyTicks);
    }

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(x: _value, y: value)) return;
            _value = value;
            _dirtyTicks = _redundancy;
            Changed?.Invoke(value);
        }
    }

    public bool IsDirty => _dirtyTicks > 0;

    public void Serialize(NetWriter writer) => _write(arg1: writer, arg2: _value);

    public void Deserialize(NetReader reader)
    {
        _value = _read(reader);
        Changed?.Invoke(_value);
    }

    public void Tick()
    {
        if (_dirtyTicks > 0) _dirtyTicks--;
    }

    public void MarkClean() => _dirtyTicks = 0;

    /// <summary>Raised on local assignment and on remote application — handy for view glue.</summary>
    public event Action<T>? Changed;

    /// <summary>
    ///     Force the dirty window open without changing the value (e.g. to resend to a newly relevant
    ///     peer).
    /// </summary>
    public void ForceDirty() => _dirtyTicks = _redundancy;

    public static implicit operator T(NetVar<T> v) => v._value;

    public override string ToString() => _value?.ToString() ?? "";
}

/// <summary>
///     Factories for <see cref="NetVar{T}" /> over the engine's common types, with sensible wire
///     encodings.
/// </summary>
public static class NetVars
{
    public static NetVar<bool> Bool(bool initial = false) => new(
        initial: initial,
        write: static (w, v) => w.WriteBool(v),
        read: static r => r.ReadBool()
    );

    public static NetVar<int> Int(int initial = 0)
    {
        return new NetVar<int>(
            initial: initial,
            write: static (w, v) => w.WriteVarInt(v),
            read: static r => r.ReadVarInt()
        );
    }

    public static NetVar<uint> UInt(uint initial = 0)
    {
        return new NetVar<uint>(
            initial: initial,
            write: static (w, v) => w.WriteVarUInt(v),
            read: static r => r.ReadVarUInt()
        );
    }

    public static NetVar<float> Float(float initial = 0)
    {
        return new NetVar<float>(
            initial: initial,
            write: static (w, v) => w.WriteSingle(v),
            read: static r => r.ReadSingle()
        );
    }

    public static NetVar<string> String(string initial = "")
    {
        return new NetVar<string>(
            initial: initial,
            write: static (w, v) => w.WriteString(v),
            read: static r => r.ReadString()
        );
    }

    public static NetVar<Vec2> Vec2(Vec2 initial = default) => new(
        initial: initial,
        write: static (w, v) => w.WriteVec2(v),
        read: static r => r.ReadVec2()
    );

    public static NetVar<Vec3> Vec3(Vec3 initial = default) => new(
        initial: initial,
        write: static (w, v) => w.WriteVec3(v),
        read: static r => r.ReadVec3()
    );

    public static NetVar<Quat> Quat(Quat initial = default)
    {
        return new NetVar<Quat>(
            initial: initial,
            write: static (w, v) => w.WriteQuaternion(v),
            read: static r => r.ReadQuaternion()
        );
    }

    public static NetVar<Color> Color(Color initial = default)
    {
        return new NetVar<Color>(
            initial: initial,
            write: static (w, v) => w.WriteColor(v),
            read: static r => r.ReadColor()
        );
    }

    /// <summary>
    ///     A position quantized into a bounded cube — far cheaper than 12 raw bytes for level-scale
    ///     coords.
    /// </summary>
    public static NetVar<Vec3> RangedVec3(Vec3 initial, float min, float max, int bits = 16)
    {
        return new NetVar<Vec3>(
            initial: initial,
            write: (w, v) => w.WriteRangedVec3(
                v: v,
                min: min,
                max: max,
                bits: bits
            ),
            read: r => r.ReadRangedVec3(min: min, max: max, bits: bits)
        );
    }
}
