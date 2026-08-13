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
            condition: MathF.Abs(a.X - b.X) < eps && MathF.Abs(a.Y - b.Y) < eps &&
                       MathF.Abs(a.Z - b.Z) < eps,
            userMessage: $"({a.X},{a.Y},{a.Z}) vs ({b.X},{b.Y},{b.Z})"
        );
    }

    [Fact]
    public void Identity_RotatesNothing()
    {
        var v = new Vec3(x: 1, y: 2, z: 3);
        AssertClose(a: v, b: Quat.Identity.RotateVec(v));
    }

    [Fact]
    public void RotateZ90_MapsXAxisToYAxis()
    {
        // Right-handed +90° about +Z sends (1,0,0) → (0,1,0).
        var q = Quat.FromAxisAngle(axis: new Vec3(x: 0, y: 0, z: 1), angleRadians: MathF.PI / 2f);
        AssertClose(a: new Vec3(x: 0, y: 1, z: 0), b: q.RotateVec(new Vec3(x: 1, y: 0, z: 0)));
    }

    [Fact]
    public void RotateVec_AgreesWithToMat4()
    {
        // The two independent rotation paths must agree for arbitrary axis/angle and vector.
        var q = Quat.FromAxisAngle(axis: new Vec3(x: 0.3f, y: -0.8f, z: 0.5f), angleRadians: 1.1f);
        var m = q.ToMat4();
        foreach (var v in new[] {
                     new Vec3(x: 1, y: 0, z: 0),
                     new Vec3(x: 0, y: 1, z: 0),
                     new Vec3(x: 0, y: 0, z: 1),
                     new Vec3(x: 1, y: 2, z: 3),
                 })
            AssertClose(a: q.RotateVec(v), b: m.MulDirection(v));
    }

    [Fact]
    public void Normalize_ProducesUnitLength()
    {
        var q = new Quat(
            x: 1,
            y: 2,
            z: 3,
            w: 4
        ).Normalize();
        float len = MathF.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W));
        Assert.True(MathF.Abs(len - 1f) < Eps);
    }

    [Fact]
    public void FromMat4_RoundTripsRotation()
    {
        var q = Quat.FromAxisAngle(axis: new Vec3(x: 0, y: 1, z: 0), angleRadians: 0.85f);
        var back = Quat.FromMat4(q.ToMat4());
        foreach (var v in new[] {
                     new Vec3(x: 1, y: 0, z: 0),
                     new Vec3(x: 0, y: 0, z: 1),
                     new Vec3(x: 2, y: -1, z: 0.5f),
                 })
            AssertClose(a: q.RotateVec(v), b: back.RotateVec(v));
    }

    [Fact]
    public void Slerp_AtEndpoints_ReturnsInputs()
    {
        var a = Quat.FromAxisAngle(axis: new Vec3(x: 0, y: 0, z: 1), angleRadians: 0.2f);
        var b = Quat.FromAxisAngle(axis: new Vec3(x: 0, y: 0, z: 1), angleRadians: 1.3f);
        var v = new Vec3(x: 1, y: 0, z: 0);
        AssertClose(a: a.RotateVec(v), b: Quat.Slerp(a: a, bIn: b, t: 0f).RotateVec(v));
        AssertClose(a: b.RotateVec(v), b: Quat.Slerp(a: a, bIn: b, t: 1f).RotateVec(v));
    }

    [Fact]
    public void Slerp_Midpoint_IsHalfwayRotation()
    {
        var a = Quat.Identity;
        var b = Quat.FromAxisAngle(axis: new Vec3(x: 0, y: 0, z: 1), angleRadians: MathF.PI / 2f);
        var mid = Quat.Slerp(a: a, bIn: b, t: 0.5f);
        // Halfway between 0° and 90° about Z is 45°: (1,0,0) → (cos45, sin45, 0).
        float s = MathF.Sqrt(0.5f);
        AssertClose(a: new Vec3(x: s, y: s, z: 0), b: mid.RotateVec(new Vec3(x: 1, y: 0, z: 0)));
    }

    [Fact]
    public void Conjugate_UndoesRotation()
    {
        var q = Quat.FromAxisAngle(axis: new Vec3(x: 0.2f, y: 0.5f, z: -0.3f), angleRadians: 0.7f);
        var v = new Vec3(x: 3, y: -1, z: 2);
        AssertClose(a: v, b: q.Conjugate().RotateVec(q.RotateVec(v)));
    }
}
