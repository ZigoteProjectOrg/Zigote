using Zigote.Core.Math3D;

namespace Zigote.Core;

public readonly struct EdgeInsets(float left, float top, float right, float bottom)
    : IEquatable<EdgeInsets>
{
    public readonly float Left = left;
    public readonly float Top = top;
    public readonly float Right = right;
    public readonly float Bottom = bottom;

    public float Horizontal => Left + Right;
    public float Vertical => Top + Bottom;

    public static EdgeInsets All(float value)
    {
        return new EdgeInsets(
            value,
            value,
            value,
            value
        );
    }

    public static EdgeInsets Symmetric(float horizontal, float vertical)
    {
        return new EdgeInsets(
            horizontal,
            vertical,
            horizontal,
            vertical
        );
    }

    public static EdgeInsets Only(float left = 0, float top = 0, float right = 0, float bottom = 0)
    {
        return new EdgeInsets(
            left,
            top,
            right,
            bottom
        );
    }

    /// <summary>Insets from explicit <c>(left, top, right, bottom)</c> edges.</summary>
    public static EdgeInsets FromLtrb(float left, float top, float right, float bottom)
    {
        return new EdgeInsets(
            left,
            top,
            right,
            bottom
        );
    }

    public static readonly EdgeInsets Zero = new(
        0,
        0,
        0,
        0
    );

    public bool Equals(EdgeInsets other)
    {
        // Exact value equality — consistent with GetHashCode. Use ApproxEquals for tolerant comparison.
        return Left.Equals(other.Left) && Top.Equals(other.Top) &&
               Right.Equals(other.Right) && Bottom.Equals(other.Bottom);
    }

    /// <summary>Tolerant component-wise comparison.</summary>
    public bool ApproxEquals(EdgeInsets other, float tolerance = Tolerance.StandardValue)
    {
        return Math.Abs(Left - other.Left) < tolerance &&
               Math.Abs(Top - other.Top) < tolerance &&
               Math.Abs(Right - other.Right) < tolerance &&
               Math.Abs(Bottom - other.Bottom) < tolerance;
    }

    public override bool Equals(object? obj)
    {
        return obj is EdgeInsets e && Equals(e);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Left,
            Top,
            Right,
            Bottom
        );
    }

    public static bool operator ==(EdgeInsets a, EdgeInsets b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(EdgeInsets a, EdgeInsets b)
    {
        return !a.Equals(b);
    }

    public override string ToString()
    {
        return $"EdgeInsets(L={Left} T={Top} R={Right} B={Bottom})";
    }
}