using Zigote.Core.Math3D;

namespace Zigote.Core;

public readonly struct Rect(float x, float y, float width, float height) : IEquatable<Rect>
{
    public readonly float X = x;
    public readonly float Y = y;
    public readonly float Width = width;
    public readonly float Height = height;

    public float Right => X + Width;
    public float Bottom => Y + Height;

    public static Rect FromLtrb(float left, float top, float right, float bottom)
    {
        return new Rect(
            left,
            top,
            right - left,
            bottom - top
        );
    }

    public bool Contains(float px, float py)
    {
        return px >= X && py >= Y && px < X + Width && py < Y + Height;
    }

    public Rect Inset(EdgeInsets e)
    {
        return new Rect(
            X + e.Left,
            Y + e.Top,
            MathF.Max(0, Width - e.Left - e.Right),
            MathF.Max(0, Height - e.Top - e.Bottom)
        );
    }

    public Rect Translate(float dx, float dy)
    {
        return new Rect(
            X + dx,
            Y + dy,
            Width,
            Height
        );
    }

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Overlaps(Rect other)
    {
        return X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
    }

    public static Rect Intersect(Rect a, Rect b)
    {
        var x = MathF.Max(a.X, b.X);
        var y = MathF.Max(a.Y, b.Y);
        var right = MathF.Min(a.Right, b.Right);
        var bottom = MathF.Min(a.Bottom, b.Bottom);
        return right > x && bottom > y
            ? new Rect(
                x,
                y,
                right - x,
                bottom - y
            )
            : Zero;
    }

    /// <summary>
    ///     Smallest rect containing both <paramref name="a" /> and <paramref name="b" />. Empty
    ///     operands are ignored.
    /// </summary>
    public static Rect Union(Rect a, Rect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var x = MathF.Min(a.X, b.X);
        var y = MathF.Min(a.Y, b.Y);
        var right = MathF.Max(a.Right, b.Right);
        var bottom = MathF.Max(a.Bottom, b.Bottom);
        return new Rect(
            x,
            y,
            right - x,
            bottom - y
        );
    }

    /// <summary>
    ///     Grow the rect by <paramref name="margin" /> on every side (negative shrinks). Never
    ///     returns a negative extent.
    /// </summary>
    public Rect Inflate(float margin)
    {
        return new Rect(
            X - margin,
            Y - margin,
            MathF.Max(0, Width + 2 * margin),
            MathF.Max(0, Height + 2 * margin)
        );
    }

    public static readonly Rect Zero = new(
        0,
        0,
        0,
        0
    );

    public bool Equals(Rect other)
    {
        // Exact value equality — consistent with GetHashCode. Use ApproxEquals for tolerant comparison.
        return X.Equals(other.X) && Y.Equals(other.Y) &&
               Width.Equals(other.Width) && Height.Equals(other.Height);
    }

    /// <summary>Tolerant component-wise comparison.</summary>
    public bool ApproxEquals(Rect other, float tolerance = Tolerance.StandardValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance &&
               Math.Abs(Width - other.Width) < tolerance &&
               Math.Abs(Height - other.Height) < tolerance;
    }

    public override bool Equals(object? obj)
    {
        return obj is Rect r && Equals(r);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            X,
            Y,
            Width,
            Height
        );
    }

    public static bool operator ==(Rect a, Rect b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Rect a, Rect b)
    {
        return !a.Equals(b);
    }

    public override string ToString()
    {
        return $"Rect({X}, {Y}, {Width}×{Height})";
    }
}
