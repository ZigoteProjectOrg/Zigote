using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Zigote.Core.Math3D;

/// <summary>
///     Small immutable tolerance value for approximate float comparisons.
///     Designed to be allocation-free and cheap to pass around.
/// </summary>
[DebuggerDisplay("{Value}")]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct Tolerance(float value) :
    IEquatable<Tolerance>,
    IComparable<Tolerance>
{
    public readonly float Value = value < 0.0f ? -value : value;

    public const float ZeroValue = 0.0f;

    /// <summary>
    ///     General-purpose float tolerance.
    ///     Good default for most transform / vector comparisons.
    /// </summary>
    public const float StandardValue = 1e-7f;

    /// <summary>
    ///     Stricter tolerance.
    ///     Warning: for float math near values around 1.0, 1e-12 is usually too small
    ///     to be meaningful. Prefer this mostly for near-zero checks or double-backed math.
    /// </summary>
    public const float PrecisionValue = 1e-12f;

    /// <summary>
    ///     More forgiving tolerance for physics/gameplay checks.
    /// </summary>
    public const float PhysicsValue = 1e-5f;

    public static readonly Tolerance Zero = new(ZeroValue);
    public static readonly Tolerance Standard = new(StandardValue);
    public static readonly Tolerance Precision = new(PrecisionValue);
    public static readonly Tolerance Physics = new(PhysicsValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsZero(float value)
    {
        return MathF.Abs(value) <= Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(float a, float b)
    {
        return MathF.Abs(a - b) <= Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool NotEquals(float a, float b)
    {
        return MathF.Abs(a - b) > Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessOrEquals(float a, float b)
    {
        return a < b || Equals(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GreaterOrEquals(float a, float b)
    {
        return a > b || Equals(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InRange(float value, float min, float max)
    {
        return value >= min - Value && value <= max + Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Tolerance other)
    {
        return Value.Equals(other.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Tolerance other)
    {
        return Value.CompareTo(other.Value);
    }

    public override bool Equals(object? obj)
    {
        return obj is Tolerance other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }

    public override string ToString()
    {
        return Value.ToString("G9");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Tolerance From(float value)
    {
        return new Tolerance(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Tolerance(float value)
    {
        return new Tolerance(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator float(Tolerance tolerance)
    {
        return tolerance.Value;
    }

    public static bool operator ==(Tolerance left, Tolerance right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Tolerance left, Tolerance right)
    {
        return !left.Equals(right);
    }

    public static bool operator <(Tolerance left, Tolerance right)
    {
        return left.Value < right.Value;
    }

    public static bool operator >(Tolerance left, Tolerance right)
    {
        return left.Value > right.Value;
    }

    public static bool operator <=(Tolerance left, Tolerance right)
    {
        return left.Value <= right.Value;
    }

    public static bool operator >=(Tolerance left, Tolerance right)
    {
        return left.Value >= right.Value;
    }
}
