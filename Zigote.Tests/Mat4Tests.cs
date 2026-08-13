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
        for (int col = 0; col < 4; col++)
        for (int row = 0; row < 4; row++)
        {
            Assert.True(
                condition: MathF.Abs(a.Get(col: col, row: row) - b.Get(col: col, row: row)) < eps,
                userMessage:
                $"element ({col},{row}): {a.Get(col: col, row: row)} vs {b.Get(col: col, row: row)}"
            );
        }
    }

    private static void AssertClose(Vec3 a, Vec3 b, float eps = Eps)
    {
        Assert.True(
            condition: MathF.Abs(a.X - b.X) < eps && MathF.Abs(a.Y - b.Y) < eps &&
                       MathF.Abs(a.Z - b.Z) < eps,
            userMessage: $"({a.X},{a.Y},{a.Z}) vs ({b.X},{b.Y},{b.Z})"
        );
    }

    [Fact]
    public void Multiply_ByIdentity_IsNoOp()
    {
        var m = new Mat4(
            c0: new Vec4(
                x: 1,
                y: 2,
                z: 3,
                w: 4
            ),
            c1: new Vec4(
                x: 5,
                y: 6,
                z: 7,
                w: 8
            ),
            c2: new Vec4(
                x: 9,
                y: 10,
                z: 11,
                w: 12
            ),
            c3: new Vec4(
                x: 13,
                y: 14,
                z: 15,
                w: 16
            )
        );
        AssertClose(a: m, b: m * Mat4.Identity);
        AssertClose(a: m, b: Mat4.Identity * m);
    }

    [Fact]
    public void Multiply_AgreesWithSequentialMulVec4()
    {
        // (A*B) applied to v must equal A applied to (B applied to v) — the defining property the
        // allocation-free operator* must preserve.
        var a = Mat4.RotationZ(0.7f);
        var b = Mat4.Translation(new Vec3(x: 2, y: -3, z: 5));
        var ab = a * b;
        var v = new Vec4(
            x: 1.5f,
            y: -2.5f,
            z: 0.25f,
            w: 1f
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
        var t = Mat4.Translation(new Vec3(x: 1, y: 2, z: 3));
        AssertClose(a: new Vec3(x: 1, y: 2, z: 3), b: t.MulPoint(new Vec3(x: 0, y: 0, z: 0)));
        AssertClose(a: new Vec3(x: 11, y: 22, z: 33), b: t.MulPoint(new Vec3(x: 10, y: 20, z: 30)));
    }

    [Fact]
    public void Scaling_ScalesPoint()
    {
        var s = Mat4.Scaling(new Vec3(x: 2, y: 3, z: 4));
        AssertClose(a: new Vec3(x: 2, y: 6, z: 12), b: s.MulPoint(new Vec3(x: 1, y: 2, z: 3)));
    }

    [Fact]
    public void Inverse_OfComposite_YieldsIdentity()
    {
        var m = Mat4.Translation(new Vec3(x: 3, y: -2, z: 5)) * Mat4.RotationY(0.9f) *
                Mat4.Scaling(new Vec3(x: 2, y: 0.5f, z: 1.5f));
        AssertClose(a: Mat4.Identity, b: m * m.Inverse());
        AssertClose(a: Mat4.Identity, b: m.Inverse() * m);
    }

    [Fact]
    public void Inverse_OfSingular_ReturnsIdentity()
    {
        var zero = new Mat4(
            c0: Vec4.Zero,
            c1: Vec4.Zero,
            c2: Vec4.Zero,
            c3: Vec4.Zero
        );
        AssertClose(a: Mat4.Identity, b: zero.Inverse());
    }

    [Fact]
    public void Transpose_IsInvolution()
    {
        var m = Mat4.RotationX(0.4f) * Mat4.Translation(new Vec3(x: 1, y: 2, z: 3));
        AssertClose(a: m, b: m.Transpose().Transpose());
    }

    [Fact]
    public void ToArray_FromArray_RoundTrips()
    {
        var m = new Mat4(
            c0: new Vec4(
                x: 1,
                y: 2,
                z: 3,
                w: 4
            ),
            c1: new Vec4(
                x: 5,
                y: 6,
                z: 7,
                w: 8
            ),
            c2: new Vec4(
                x: 9,
                y: 10,
                z: 11,
                w: 12
            ),
            c3: new Vec4(
                x: 13,
                y: 14,
                z: 15,
                w: 16
            )
        );
        AssertClose(a: m, b: Mat4.FromArray(m.ToArray()));
    }

    [Fact]
    public void Transform3D_ToMat4_MatchesTranslationRotationScaling()
    {
        // The closed-form Transform3D.ToMat4() must equal the reference Translation * Rotation *
        // Scaling composition for arbitrary translation / rotation / scale (incl. non-uniform and
        // negative scale) — this guards the DOD rewrite that dropped the two 4×4 products.
        (Vec3 Pos, Quat Rot, Vec3 Scale)[] cases = [
            (new Vec3(x: 0, y: 0, z: 0), Quat.Identity, new Vec3(x: 1, y: 1, z: 1)),
            (new Vec3(x: 3, y: -2, z: 5),
                Quat.FromAxisAngle(axis: new Vec3(x: 0, y: 1, z: 0), angleRadians: 0.9f),
                new Vec3(x: 2, y: 0.5f, z: 1.5f)),
            (new Vec3(x: -7.5f, y: 4.25f, z: 1f),
                Quat.FromAxisAngle(
                    axis: new Vec3(x: 1, y: 2, z: 3).Normalize(),
                    angleRadians: 2.1f
                ),
                new Vec3(x: 0.3f, y: 3f, z: -1.2f)),
            (new Vec3(x: 100, y: 0.01f, z: -50),
                Quat.FromAxisAngle(
                    axis: new Vec3(x: -1, y: 0.5f, z: 2).Normalize(),
                    angleRadians: -1.3f
                ),
                new Vec3(x: 1, y: 1, z: 1)),
        ];

        foreach (var (pos, rot, scale) in cases)
        {
            var reference = Mat4.Translation(pos) * rot.ToMat4() * Mat4.Scaling(scale);
            var actual = new Transform3D(position: pos, rotation: rot, scale: scale).ToMat4();
            AssertClose(a: reference, b: actual);
        }
    }

    [Fact]
    public void Perspective_PutsPointInExpectedClipRange()
    {
        var p = Mat4.PerspectiveRhZo(
            fovyRadians: MathF.PI / 2f,
            aspect: 1f,
            near: 0.1f,
            far: 100f
        );
        // A point on the -Z axis inside the frustum maps to z/w within [0,1] (wgpu clip).
        var clip = p.MulVec4(
            new Vec4(
                x: 0,
                y: 0,
                z: -1f,
                w: 1f
            )
        );
        float ndcZ = clip.Z / clip.W;
        Assert.InRange(actual: ndcZ, low: 0f, high: 1f);
    }
}
