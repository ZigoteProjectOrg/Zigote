using Xunit;
using Zigote.Core.Math3D;

namespace Zigote.Tests;

/// <summary>
///     Covers the hand-rolled quaternion math — easy to break silently, used for every node
///     rotation.
/// </summary>
public class QuatTests
{
    private const float Eps = 1e-4f;

    private static void AssertClose(Vec3 a, Vec3 b, float eps = Eps)
    {
        Assert.True(
            MathF.Abs(a.X - b.X) < eps && MathF.Abs(a.Y - b.Y) < eps && MathF.Abs(a.Z - b.Z) < eps,
            $"({a.X},{a.Y},{a.Z}) vs ({b.X},{b.Y},{b.Z})"
        );
    }

    [Fact]
    public void Identity_RotatesNothing()
    {
        var v = new Vec3(1, 2, 3);
        AssertClose(v, Quat.Identity.RotateVec(v));
    }

    [Fact]
    public void RotateZ90_MapsXAxisToYAxis()
    {
        // Right-handed +90° about +Z sends (1,0,0) → (0,1,0).
        var q = Quat.FromAxisAngle(new Vec3(0, 0, 1), MathF.PI / 2f);
        AssertClose(new Vec3(0, 1, 0), q.RotateVec(new Vec3(1, 0, 0)));
    }

    [Fact]
    public void RotateVec_AgreesWithToMat4()
    {
        // The two independent rotation paths must agree for arbitrary axis/angle and vector.
        var q = Quat.FromAxisAngle(new Vec3(0.3f, -0.8f, 0.5f), 1.1f);
        var m = q.ToMat4();
        foreach (var v in new[] {
                     new Vec3(1, 0, 0),
                     new Vec3(0, 1, 0),
                     new Vec3(0, 0, 1),
                     new Vec3(1, 2, 3),
                 })
            AssertClose(q.RotateVec(v), m.MulDirection(v));
    }

    [Fact]
    public void Normalize_ProducesUnitLength()
    {
        var q = new Quat(
            1,
            2,
            3,
            4
        ).Normalize();
        var len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        Assert.True(MathF.Abs(len - 1f) < Eps);
    }

    [Fact]
    public void FromMat4_RoundTripsRotation()
    {
        var q = Quat.FromAxisAngle(new Vec3(0, 1, 0), 0.85f);
        var back = Quat.FromMat4(q.ToMat4());
        foreach (var v in new[] {
                     new Vec3(1, 0, 0),
                     new Vec3(0, 0, 1),
                     new Vec3(2, -1, 0.5f),
                 })
            AssertClose(q.RotateVec(v), back.RotateVec(v));
    }

    [Fact]
    public void Slerp_AtEndpoints_ReturnsInputs()
    {
        var a = Quat.FromAxisAngle(new Vec3(0, 0, 1), 0.2f);
        var b = Quat.FromAxisAngle(new Vec3(0, 0, 1), 1.3f);
        var v = new Vec3(1, 0, 0);
        AssertClose(a.RotateVec(v), Quat.Slerp(a, b, 0f).RotateVec(v));
        AssertClose(b.RotateVec(v), Quat.Slerp(a, b, 1f).RotateVec(v));
    }

    [Fact]
    public void Slerp_Midpoint_IsHalfwayRotation()
    {
        var a = Quat.Identity;
        var b = Quat.FromAxisAngle(new Vec3(0, 0, 1), MathF.PI / 2f);
        var mid = Quat.Slerp(a, b, 0.5f);
        // Halfway between 0° and 90° about Z is 45°: (1,0,0) → (cos45, sin45, 0).
        var s = MathF.Sqrt(0.5f);
        AssertClose(new Vec3(s, s, 0), mid.RotateVec(new Vec3(1, 0, 0)));
    }

    [Fact]
    public void Conjugate_UndoesRotation()
    {
        var q = Quat.FromAxisAngle(new Vec3(0.2f, 0.5f, -0.3f), 0.7f);
        var v = new Vec3(3, -1, 2);
        AssertClose(v, q.Conjugate().RotateVec(q.RotateVec(v)));
    }
}
