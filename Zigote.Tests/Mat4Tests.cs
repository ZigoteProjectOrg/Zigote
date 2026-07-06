using Xunit;
using Zigote.Core.Math3D;

namespace Zigote.Tests;

/// <summary>
///     Covers the hand-rolled 4×4 matrix math, and specifically guards the allocation-free rewrites of
///     <see cref="Mat4.operator*" /> (now four <see cref="Mat4.MulVec4" /> calls) and
///     <see cref="Mat4.Inverse" /> (now a stack buffer) — the numeric results must be unchanged.
/// </summary>
public class Mat4Tests
{
    private const float Eps = 1e-4f;

    private static void AssertClose(Mat4 a, Mat4 b, float eps = Eps)
    {
        for (var col = 0; col < 4; col++)
        for (var row = 0; row < 4; row++)
            Assert.True(
                MathF.Abs(a.Get(col, row) - b.Get(col, row)) < eps,
                $"element ({col},{row}): {a.Get(col, row)} vs {b.Get(col, row)}"
            );
    }

    private static void AssertClose(Vec3 a, Vec3 b, float eps = Eps)
    {
        Assert.True(
            MathF.Abs(a.X - b.X) < eps && MathF.Abs(a.Y - b.Y) < eps && MathF.Abs(a.Z - b.Z) < eps,
            $"({a.X},{a.Y},{a.Z}) vs ({b.X},{b.Y},{b.Z})"
        );
    }

    [Fact]
    public void Multiply_ByIdentity_IsNoOp()
    {
        var m = new Mat4(
            new Vec4(
                1,
                2,
                3,
                4
            ),
            new Vec4(
                5,
                6,
                7,
                8
            ),
            new Vec4(
                9,
                10,
                11,
                12
            ),
            new Vec4(
                13,
                14,
                15,
                16
            )
        );
        AssertClose(m, m * Mat4.Identity);
        AssertClose(m, Mat4.Identity * m);
    }

    [Fact]
    public void Multiply_AgreesWithSequentialMulVec4()
    {
        // (A*B) applied to v must equal A applied to (B applied to v) — the defining property the
        // allocation-free operator* must preserve.
        var a = Mat4.RotationZ(0.7f);
        var b = Mat4.Translation(new Vec3(2, -3, 5));
        var ab = a * b;
        var v = new Vec4(
            1.5f,
            -2.5f,
            0.25f,
            1f
        );

        var direct = ab.MulVec4(v);
        var seq = a.MulVec4(b.MulVec4(v));
        Assert.True(MathF.Abs(direct.X - seq.X) < Eps);
        Assert.True(MathF.Abs(direct.Y - seq.Y) < Eps);
        Assert.True(MathF.Abs(direct.Z - seq.Z) < Eps);
        Assert.True(MathF.Abs(direct.W - seq.W) < Eps);
    }

    [Fact]
    public void Translation_MovesPoint()
    {
        var t = Mat4.Translation(new Vec3(1, 2, 3));
        AssertClose(new Vec3(1, 2, 3), t.MulPoint(new Vec3(0, 0, 0)));
        AssertClose(new Vec3(11, 22, 33), t.MulPoint(new Vec3(10, 20, 30)));
    }

    [Fact]
    public void Scaling_ScalesPoint()
    {
        var s = Mat4.Scaling(new Vec3(2, 3, 4));
        AssertClose(new Vec3(2, 6, 12), s.MulPoint(new Vec3(1, 2, 3)));
    }

    [Fact]
    public void Inverse_OfComposite_YieldsIdentity()
    {
        var m = Mat4.Translation(new Vec3(3, -2, 5)) * Mat4.RotationY(0.9f) *
                Mat4.Scaling(new Vec3(2, 0.5f, 1.5f));
        AssertClose(Mat4.Identity, m * m.Inverse());
        AssertClose(Mat4.Identity, m.Inverse() * m);
    }

    [Fact]
    public void Inverse_OfSingular_ReturnsIdentity()
    {
        var zero = new Mat4(
            Vec4.Zero,
            Vec4.Zero,
            Vec4.Zero,
            Vec4.Zero
        );
        AssertClose(Mat4.Identity, zero.Inverse());
    }

    [Fact]
    public void Transpose_IsInvolution()
    {
        var m = Mat4.RotationX(0.4f) * Mat4.Translation(new Vec3(1, 2, 3));
        AssertClose(m, m.Transpose().Transpose());
    }

    [Fact]
    public void ToArray_FromArray_RoundTrips()
    {
        var m = new Mat4(
            new Vec4(
                1,
                2,
                3,
                4
            ),
            new Vec4(
                5,
                6,
                7,
                8
            ),
            new Vec4(
                9,
                10,
                11,
                12
            ),
            new Vec4(
                13,
                14,
                15,
                16
            )
        );
        AssertClose(m, Mat4.FromArray(m.ToArray()));
    }

    [Fact]
    public void Transform3D_ToMat4_MatchesTranslationRotationScaling()
    {
        // The closed-form Transform3D.ToMat4() must equal the reference Translation * Rotation *
        // Scaling composition for arbitrary translation / rotation / scale (incl. non-uniform and
        // negative scale) — this guards the DOD rewrite that dropped the two 4×4 products.
        (Vec3 Pos, Quat Rot, Vec3 Scale)[] cases = [
            (new Vec3(0, 0, 0), Quat.Identity, new Vec3(1, 1, 1)),
            (new Vec3(3, -2, 5), Quat.FromAxisAngle(new Vec3(0, 1, 0), 0.9f),
                new Vec3(2, 0.5f, 1.5f)),
            (new Vec3(-7.5f, 4.25f, 1f), Quat.FromAxisAngle(new Vec3(1, 2, 3).Normalize(), 2.1f),
                new Vec3(0.3f, 3f, -1.2f)),
            (new Vec3(100, 0.01f, -50),
                Quat.FromAxisAngle(new Vec3(-1, 0.5f, 2).Normalize(), -1.3f),
                new Vec3(1, 1, 1)),
        ];

        foreach (var (pos, rot, scale) in cases)
        {
            var reference = Mat4.Translation(pos) * rot.ToMat4() * Mat4.Scaling(scale);
            var actual = new Transform3D(pos, rot, scale).ToMat4();
            AssertClose(reference, actual);
        }
    }

    [Fact]
    public void Perspective_PutsPointInExpectedClipRange()
    {
        var p = Mat4.PerspectiveRhZo(
            MathF.PI / 2f,
            1f,
            0.1f,
            100f
        );
        // A point on the -Z axis inside the frustum maps to z/w within [0,1] (wgpu clip).
        var clip = p.MulVec4(
            new Vec4(
                0,
                0,
                -1f,
                1f
            )
        );
        var ndcZ = clip.Z / clip.W;
        Assert.InRange(ndcZ, 0f, 1f);
    }
}