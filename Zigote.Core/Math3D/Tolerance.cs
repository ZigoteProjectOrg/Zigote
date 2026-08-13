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
    public bool IsZero(float value) => MathF.Abs(value) <= Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(float a, float b) => MathF.Abs(a - b) <= Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool NotEquals(float a, float b) => MathF.Abs(a - b) > Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessOrEquals(float a, float b) => a < b || Equals(a: a, b: b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GreaterOrEquals(float a, float b) => a > b || Equals(a: a, b: b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InRange(float value, float min, float max) =>
        value >= min - Value && value <= max + Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Tolerance other) => Value.Equals(other.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Tolerance other) => Value.CompareTo(other.Value);

    public override bool Equals(object? obj) => obj is Tolerance other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value.ToString("G9");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Tolerance From(float value) => new(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Tolerance(float value) => new(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator float(Tolerance tolerance) => tolerance.Value;

    public static bool operator ==(Tolerance left, Tolerance right) => left.Equals(right);

    public static bool operator !=(Tolerance left, Tolerance right) => !left.Equals(right);

    public static bool operator <(Tolerance left, Tolerance right) => left.Value < right.Value;

    public static bool operator >(Tolerance left, Tolerance right) => left.Value > right.Value;

    public static bool operator <=(Tolerance left, Tolerance right) => left.Value <= right.Value;

    public static bool operator >=(Tolerance left, Tolerance right) => left.Value >= right.Value;
}
