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
            x: left,
            y: top,
            width: right - left,
            height: bottom - top
        );
    }

    public bool Contains(float px, float py) =>
        px >= X && py >= Y && px < X + Width && py < Y + Height;

    public Rect Inset(EdgeInsets e)
    {
        return new Rect(
            x: X + e.Left,
            y: Y + e.Top,
            width: MathF.Max(x: 0, y: Width - e.Left - e.Right),
            height: MathF.Max(x: 0, y: Height - e.Top - e.Bottom)
        );
    }

    public Rect Translate(float dx, float dy)
    {
        return new Rect(
            x: X + dx,
            y: Y + dy,
            width: Width,
            height: Height
        );
    }

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Overlaps(Rect other) =>
        X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

    public static Rect Intersect(Rect a, Rect b)
    {
        float x = MathF.Max(x: a.X, y: b.X);
        float y = MathF.Max(x: a.Y, y: b.Y);
        float right = MathF.Min(x: a.Right, y: b.Right);
        float bottom = MathF.Min(x: a.Bottom, y: b.Bottom);
        return right > x && bottom > y
            ? new Rect(
                x: x,
                y: y,
                width: right - x,
                height: bottom - y
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
        float x = MathF.Min(x: a.X, y: b.X);
        float y = MathF.Min(x: a.Y, y: b.Y);
        float right = MathF.Max(x: a.Right, y: b.Right);
        float bottom = MathF.Max(x: a.Bottom, y: b.Bottom);
        return new Rect(
            x: x,
            y: y,
            width: right - x,
            height: bottom - y
        );
    }

    /// <summary>
    ///     Grow the rect by <paramref name="margin" /> on every side (negative shrinks). Never
    ///     returns a negative extent.
    /// </summary>
    public Rect Inflate(float margin)
    {
        return new Rect(
            x: X - margin,
            y: Y - margin,
            width: MathF.Max(x: 0, y: Width + (2 * margin)),
            height: MathF.Max(x: 0, y: Height + (2 * margin))
        );
    }

    public static readonly Rect Zero = new(
        x: 0,
        y: 0,
        width: 0,
        height: 0
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

    public override bool Equals(object? obj) => obj is Rect r && Equals(r);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            value1: X,
            value2: Y,
            value3: Width,
            value4: Height
        );
    }

    public static bool operator ==(Rect a, Rect b) => a.Equals(b);

    public static bool operator !=(Rect a, Rect b) => !a.Equals(b);

    public override string ToString() => $"Rect({X}, {Y}, {Width}×{Height})";
}
