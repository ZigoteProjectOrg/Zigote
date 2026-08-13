using Xunit;
using Zigote.Core.Math3D;

namespace Zigote.Tests;

public class FrustumTests
{
    // A camera at the origin looking down -Z (RH), 90° fov, building the same proj*view the
    // renderer uses (column-major, zero-to-one depth).
    private static Frustum MakeFrustum(Vec3 eye, Vec3 target, float fov = MathF.PI / 2f,
        float aspect = 1f, float near = 0.1f, float far = 1000f)
    {
        var view = Mat4.LookAt(eye, target, new Vec3(0, 1, 0));
        var proj = Mat4.PerspectiveRhZo(
            fov,
            aspect,
            near,
            far
        );
        return Frustum.FromViewProjection(proj * view);
    }

    [Fact]
    public void Point_In_Front_Is_Inside()
    {
        var f = MakeFrustum(Vec3.Zero, new Vec3(0, 0, -1));
        Assert.True(f.IntersectsSphere(new Vec3(0, 0, -10), 1f));
    }

    [Fact]
    public void Point_Behind_Is_Outside()
    {
        var f = MakeFrustum(Vec3.Zero, new Vec3(0, 0, -1));
        Assert.False(f.IntersectsSphere(new Vec3(0, 0, 10), 1f)); // behind the camera
    }

    [Fact]
    public void Point_Far_Beyond_Far_Plane_Is_Outside()
    {
        var f = MakeFrustum(Vec3.Zero, new Vec3(0, 0, -1), far: 100f);
        Assert.False(f.IntersectsSphere(new Vec3(0, 0, -500), 1f));
    }

    [Fact]
    public void Point_Far_To_The_Side_Is_Outside()
    {
        var f = MakeFrustum(Vec3.Zero, new Vec3(0, 0, -1), MathF.PI / 4f);
        // Well outside a 45° cone at z=-10.
        Assert.False(f.IntersectsSphere(new Vec3(1000, 0, -10), 1f));
    }

    [Fact]
    public void Large_Sphere_Straddling_The_Edge_Is_Kept()
    {
        var f = MakeFrustum(Vec3.Zero, new Vec3(0, 0, -1), MathF.PI / 4f);
        // Centre just outside the side plane, but a big radius still overlaps the frustum → visible.
        Assert.True(f.IntersectsSphere(new Vec3(6, 0, -10), 3f));
    }

    [Fact]
    public void Belt_Style_Camera_Sees_Front_Culls_Behind()
    {
        // Mirrors the asteroid benchmark: camera high and back, looking at the origin.
        var eye = new Vec3(0, 350, 950);
        var f = MakeFrustum(
            eye,
            Vec3.Zero,
            MathF.PI / 4f,
            16f / 9f,
            0.1f,
            4000f
        );
        Assert.True(f.IntersectsSphere(new Vec3(0, 0, 0), 10f)); // belt centre, on-screen
        Assert.True(f.IntersectsSphere(new Vec3(0, 0, 400), 10f)); // near edge toward camera
        Assert.False(
            f.IntersectsSphere(new Vec3(0, 0, 3000), 10f)
        ); // far behind the belt, off-screen
    }
}
