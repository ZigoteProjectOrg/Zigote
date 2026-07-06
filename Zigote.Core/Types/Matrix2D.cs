namespace Zigote.Core;

/// <summary>
///     A 2-D affine transform (2×3 matrix) in SVG/Canvas coefficient order:
///     <c>x' = A·x + C·y + Tx</c>, <c>y' = B·x + D·y + Ty</c>. Y grows downward (screen space),
///     so a positive <see cref="Rotation" /> angle turns clockwise on screen.
///     Backs <c>PaintList.PushTransform</c> (native paint-path transform stack) and the
///     <c>Transform</c> widget's scale/rotation.
/// </summary>
public readonly struct Matrix2D(float a, float b, float c, float d, float tx, float ty)
    : IEquatable<Matrix2D>
{
    public readonly float A = a;
    public readonly float B = b;
    public readonly float C = c;
    public readonly float D = d;
    public readonly float Tx = tx;
    public readonly float Ty = ty;

    public static readonly Matrix2D Identity = new(
        1,
        0,
        0,
        1,
        0,
        0
    );

    public bool IsIdentity => A == 1f && B == 0f && C == 0f && D == 1f && Tx == 0f && Ty == 0f;

    public static Matrix2D Translation(float dx, float dy)
    {
        return new Matrix2D(
            1,
            0,
            0,
            1,
            dx,
            dy
        );
    }

    public static Matrix2D Scale(float sx, float sy)
    {
        return new Matrix2D(
            sx,
            0,
            0,
            sy,
            0,
            0
        );
    }

    /// <summary>Rotation by <paramref name="radians" /> around the origin (clockwise on screen, y-down).</summary>
    public static Matrix2D Rotation(float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Matrix2D(
            cos,
            sin,
            -sin,
            cos,
            0,
            0
        );
    }

    /// <summary>
    ///     Function composition: <c>(m1 * m2).Apply(p) == m1.Apply(m2.Apply(p))</c> —
    ///     the right operand applies first.
    /// </summary>
    public static Matrix2D operator *(Matrix2D m1, Matrix2D m2)
    {
        return new Matrix2D(
            m1.A * m2.A + m1.C * m2.B,
            m1.B * m2.A + m1.D * m2.B,
            m1.A * m2.C + m1.C * m2.D,
            m1.B * m2.C + m1.D * m2.D,
            m1.A * m2.Tx + m1.C * m2.Ty + m1.Tx,
            m1.B * m2.Tx + m1.D * m2.Ty + m1.Ty
        );
    }

    public Offset Apply(Offset p)
    {
        return new Offset(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);
    }

    public float Determinant => A * D - B * C;

    /// <summary>
    ///     Inverse transform, or <c>false</c> when the matrix is singular (e.g. scale 0) —
    ///     a degenerate transform maps everything onto a line/point, so no point maps back.
    /// </summary>
    public bool TryInvert(out Matrix2D inverse)
    {
        var det = Determinant;
        if (det == 0f || !float.IsFinite(det))
        {
            inverse = Identity;
            return false;
        }

        var inv = 1f / det;
        var ia = D * inv;
        var ib = -B * inv;
        var ic = -C * inv;
        var id = A * inv;
        inverse = new Matrix2D(
            ia,
            ib,
            ic,
            id,
            -(ia * Tx + ic * Ty),
            -(ib * Tx + id * Ty)
        );
        return true;
    }

    public bool Equals(Matrix2D other)
    {
        return A.Equals(other.A) && B.Equals(other.B) && C.Equals(other.C) &&
               D.Equals(other.D) && Tx.Equals(other.Tx) && Ty.Equals(other.Ty);
    }

    public override bool Equals(object? obj)
    {
        return obj is Matrix2D m && Equals(m);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            A,
            B,
            C,
            D,
            Tx,
            Ty
        );
    }

    public static bool operator ==(Matrix2D a, Matrix2D b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Matrix2D a, Matrix2D b)
    {
        return !a.Equals(b);
    }

    public override string ToString()
    {
        return $"Matrix2D(a:{A}, b:{B}, c:{C}, d:{D}, tx:{Tx}, ty:{Ty})";
    }
}