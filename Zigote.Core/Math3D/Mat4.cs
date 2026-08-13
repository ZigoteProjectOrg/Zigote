namespace Zigote.Core.Math3D;

/// <summary>
///     Column-major 4×4 matrix. <c>Col[i]</c> is the i-th column.
///     Matches wgpu/Metal/Vulkan shader layout (column_major storage).
/// </summary>
public readonly struct Mat4(Vec4 c0, Vec4 c1, Vec4 c2, Vec4 c3)
    : IEquatable<Mat4>
{
    public Vec4 Col0 { get; } = c0;
    public Vec4 Col1 { get; } = c1;
    public Vec4 Col2 { get; } = c2;
    public Vec4 Col3 { get; } = c3;

    public static readonly Mat4 Identity = new(
        new Vec4(
            1,
            0,
            0,
            0
        ),
        new Vec4(
            0,
            1,
            0,
            0
        ),
        new Vec4(
            0,
            0,
            1,
            0
        ),
        new Vec4(
            0,
            0,
            0,
            1
        )
    );

    private Vec4 GetCol(int c)
    {
        return c switch { 0 => Col0, 1 => Col1, 2 => Col2, 3 => Col3, _ => Vec4.Zero };
    }

    public float Get(int col, int row)
    {
        return GetCol(col).GetComp(row);
    }

    // ── Arithmetic ───────────────────────────────────────────────────────────

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        // Each result column is A applied to the corresponding column of B.
        // Equivalent to the column-major triple-loop but allocation-free (no temp array).
        return new Mat4(
            a.MulVec4(b.Col0),
            a.MulVec4(b.Col1),
            a.MulVec4(b.Col2),
            a.MulVec4(b.Col3)
        );
    }

    public Vec4 MulVec4(Vec4 v)
    {
        // Column-major M·v is the linear combination of M's columns weighted by v's components.
        // Each term is a SIMD Vector4 scale and the sum is SIMD — identical math to the scalar
        // form, zero convention risk. operator* calls this per column, so it benefits too.
        return new Vec4(Col0.V * v.X + Col1.V * v.Y + Col2.V * v.Z + Col3.V * v.W);
    }

    public Vec3 MulPoint(Vec3 v)
    {
        var r = MulVec4(v.ToVec4(1f));
        return MathF.Abs(r.W) > float.Epsilon ? r.Xyz() * (1f / r.W) : r.Xyz();
    }

    public Vec3 MulDirection(Vec3 v)
    {
        var r = MulVec4(v.ToVec4(0f));
        return r.Xyz();
    }

    // ── Decompositions ───────────────────────────────────────────────────────

    public Mat4 Transpose()
    {
        return new Mat4(
            new Vec4(
                Col0.X,
                Col1.X,
                Col2.X,
                Col3.X
            ),
            new Vec4(
                Col0.Y,
                Col1.Y,
                Col2.Y,
                Col3.Y
            ),
            new Vec4(
                Col0.Z,
                Col1.Z,
                Col2.Z,
                Col3.Z
            ),
            new Vec4(
                Col0.W,
                Col1.W,
                Col2.W,
                Col3.W
            )
        );
    }

    /// <summary>General 4×4 inverse via Gaussian elimination with partial pivoting.</summary>
    public Mat4 Inverse()
    {
        // Augmented matrix [A | I], 4 rows × 8 cols, on the stack (row-major: a[r * 8 + c]).
        Span<float> a = stackalloc float[4 * 8];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++) a[r * 8 + c] = Get(c, r);
            a[r * 8 + 4 + r] = 1f;
        }

        for (var col = 0; col < 4; col++)
        {
            var maxRow = col;
            var maxVal = MathF.Abs(a[col * 8 + col]);
            for (var row = col + 1; row < 4; row++)
                if (MathF.Abs(a[row * 8 + col]) > maxVal)
                {
                    maxVal = MathF.Abs(a[row * 8 + col]);
                    maxRow = row;
                }

            if (maxVal < float.Epsilon) return Identity;

            if (maxRow != col)
                for (var j = 0; j < 8; j++)
                    (a[col * 8 + j], a[maxRow * 8 + j]) = (a[maxRow * 8 + j], a[col * 8 + j]);

            var invPiv = 1f / a[col * 8 + col];
            for (var j = 0; j < 8; j++) a[col * 8 + j] *= invPiv;

            for (var row = 0; row < 4; row++)
            {
                if (row == col) continue;
                var factor = a[row * 8 + col];
                for (var j = 0; j < 8; j++) a[row * 8 + j] -= factor * a[col * 8 + j];
            }
        }

        return new Mat4(
            new Vec4(
                a[0 * 8 + 4],
                a[1 * 8 + 4],
                a[2 * 8 + 4],
                a[3 * 8 + 4]
            ),
            new Vec4(
                a[0 * 8 + 5],
                a[1 * 8 + 5],
                a[2 * 8 + 5],
                a[3 * 8 + 5]
            ),
            new Vec4(
                a[0 * 8 + 6],
                a[1 * 8 + 6],
                a[2 * 8 + 6],
                a[3 * 8 + 6]
            ),
            new Vec4(
                a[0 * 8 + 7],
                a[1 * 8 + 7],
                a[2 * 8 + 7],
                a[3 * 8 + 7]
            )
        );
    }

    // ── Constructors ─────────────────────────────────────────────────────────

    public static Mat4 Translation(Vec3 v)
    {
        var m = Identity;
        return new Mat4(
            m.Col0,
            m.Col1,
            m.Col2,
            new Vec4(
                v.X,
                v.Y,
                v.Z,
                1f
            )
        );
    }

    public static Mat4 Scaling(Vec3 v)
    {
        return new Mat4(
            new Vec4(
                v.X,
                0,
                0,
                0
            ),
            new Vec4(
                0,
                v.Y,
                0,
                0
            ),
            new Vec4(
                0,
                0,
                v.Z,
                0
            ),
            new Vec4(
                0,
                0,
                0,
                1
            )
        );
    }

    public static Mat4 RotationX(float angle)
    {
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        return new Mat4(
            new Vec4(
                1,
                0,
                0,
                0
            ),
            new Vec4(
                0,
                c,
                s,
                0
            ),
            new Vec4(
                0,
                -s,
                c,
                0
            ),
            new Vec4(
                0,
                0,
                0,
                1
            )
        );
    }

    public static Mat4 RotationY(float angle)
    {
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        return new Mat4(
            new Vec4(
                c,
                0,
                -s,
                0
            ),
            new Vec4(
                0,
                1,
                0,
                0
            ),
            new Vec4(
                s,
                0,
                c,
                0
            ),
            new Vec4(
                0,
                0,
                0,
                1
            )
        );
    }

    public static Mat4 RotationZ(float angle)
    {
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        return new Mat4(
            new Vec4(
                c,
                s,
                0,
                0
            ),
            new Vec4(
                -s,
                c,
                0,
                0
            ),
            new Vec4(
                0,
                0,
                1,
                0
            ),
            new Vec4(
                0,
                0,
                0,
                1
            )
        );
    }

    /// <summary>
    ///     Right-handed perspective projection mapping near→z=0, far→z=1 (wgpu clip space).
    /// </summary>
    public static Mat4 PerspectiveRhZo(float fovyRadians, float aspect, float near, float far)
    {
        var f = 1f / MathF.Tan(fovyRadians * 0.5f);
        return new Mat4(
            new Vec4(
                f / aspect,
                0,
                0,
                0
            ),
            new Vec4(
                0,
                f,
                0,
                0
            ),
            new Vec4(
                0,
                0,
                far / (near - far),
                -1f
            ),
            new Vec4(
                0,
                0,
                near * far / (near - far),
                0
            )
        );
    }

    /// <summary>Orthographic projection mapping near→0, far→1 (wgpu clip space).</summary>
    public static Mat4 OrthographicRhZo(float left, float right, float bottom, float top,
        float near, float far)
    {
        return new Mat4(
            new Vec4(
                2f / (right - left),
                0,
                0,
                0
            ),
            new Vec4(
                0,
                2f / (top - bottom),
                0,
                0
            ),
            new Vec4(
                0,
                0,
                1f / (near - far),
                0
            ),
            new Vec4(
                -(right + left) / (right - left),
                -(top + bottom) / (top - bottom),
                near / (near - far),
                1f
            )
        );
    }

    /// <summary>Right-handed look-at view matrix.</summary>
    public static Mat4 LookAt(Vec3 eye, Vec3 center, Vec3 worldUp)
    {
        var f = (center - eye).Normalize();
        var r = f.Cross(worldUp).Normalize();
        var u = r.Cross(f);

        return new Mat4(
            new Vec4(
                r.X,
                u.X,
                -f.X,
                0
            ),
            new Vec4(
                r.Y,
                u.Y,
                -f.Y,
                0
            ),
            new Vec4(
                r.Z,
                u.Z,
                -f.Z,
                0
            ),
            new Vec4(
                -r.Dot(eye),
                -u.Dot(eye),
                f.Dot(eye),
                1f
            )
        );
    }

    // ── Serialisation ────────────────────────────────────────────────────────

    /// <summary>Flat array in column-major order, suitable for GPU uniform upload.</summary>
    public float[] ToArray()
    {
        return [
            Col0.X, Col0.Y, Col0.Z, Col0.W,
            Col1.X, Col1.Y, Col1.Z, Col1.W,
            Col2.X, Col2.Y, Col2.Z, Col2.W,
            Col3.X, Col3.Y, Col3.Z, Col3.W,
        ];
    }

    public static Mat4 FromArray(float[] a)
    {
        return new Mat4(
            new Vec4(
                a[0],
                a[1],
                a[2],
                a[3]
            ),
            new Vec4(
                a[4],
                a[5],
                a[6],
                a[7]
            ),
            new Vec4(
                a[8],
                a[9],
                a[10],
                a[11]
            ),
            new Vec4(
                a[12],
                a[13],
                a[14],
                a[15]
            )
        );
    }

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(Mat4 other)
    {
        return Col0 == other.Col0 && Col1 == other.Col1 && Col2 == other.Col2 && Col3 == other.Col3;
    }

    public override bool Equals(object? obj)
    {
        return obj is Mat4 m && Equals(m);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Col0,
            Col1,
            Col2,
            Col3
        );
    }

    public static bool operator ==(Mat4 a, Mat4 b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Mat4 a, Mat4 b)
    {
        return !a.Equals(b);
    }
}
