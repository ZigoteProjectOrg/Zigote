namespace Zigote.Core.Math3D;

/// <summary>
///     A view frustum as six world-space planes, extracted from a view-projection matrix
///     (Gribb–Hartmann). Plane normals point INTO the frustum; a point is inside when
///     <c>n·p + d &gt;= 0</c> for all six. Built for a zero-to-one depth clip space (wgpu/Metal/D3D),
///     matching <see cref="Mat4.PerspectiveRhZo" />.
///     Used for frustum culling: build once per frame from the camera's view-projection, then test
///     each object's bounding sphere with <see cref="IntersectsSphere" />.
/// </summary>
public readonly struct Frustum
{
    // Each plane stored as (nx, ny, nz, d), normalized so nx²+ny²+nz²=1.
    private readonly Vec4 _left, _right, _bottom, _top, _near, _far;

    private Frustum(Vec4 left, Vec4 right, Vec4 bottom, Vec4 top, Vec4 near, Vec4 far)
    {
        _left = left;
        _right = right;
        _bottom = bottom;
        _top = top;
        _near = near;
        _far = far;
    }

    /// <summary>
    ///     Extract the six frustum planes from a view-projection matrix (proj * view), column-major,
    ///     zero-to-one depth. Pass the SAME matrix the renderer uses so culling matches what is drawn.
    /// </summary>
    public static Frustum FromViewProjection(Mat4 vp)
    {
        // Rows of the matrix (row r = the r-th component of clip = M * worldPos).
        var r0 = new Vec4(
            vp.Get(0, 0),
            vp.Get(1, 0),
            vp.Get(2, 0),
            vp.Get(3, 0)
        );
        var r1 = new Vec4(
            vp.Get(0, 1),
            vp.Get(1, 1),
            vp.Get(2, 1),
            vp.Get(3, 1)
        );
        var r2 = new Vec4(
            vp.Get(0, 2),
            vp.Get(1, 2),
            vp.Get(2, 2),
            vp.Get(3, 2)
        );
        var r3 = new Vec4(
            vp.Get(0, 3),
            vp.Get(1, 3),
            vp.Get(2, 3),
            vp.Get(3, 3)
        );

        // Gribb–Hartmann for [0,1] clip-z: side planes from w±axis, near = z, far = w − z.
        return new Frustum(
            Normalize(r3 + r0), // left
            Normalize(r3 - r0), // right
            Normalize(r3 + r1), // bottom
            Normalize(r3 - r1), // top
            Normalize(r2), // near
            Normalize(r3 - r2)
        ); // far
    }

    private static Vec4 Normalize(Vec4 p)
    {
        var len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        if (len < 1e-20f) return p;
        var inv = 1f / len;
        return new Vec4(
            p.X * inv,
            p.Y * inv,
            p.Z * inv,
            p.W * inv
        );
    }

    private static bool Outside(Vec4 plane, Vec3 c, float r)
    {
        return plane.X * c.X + plane.Y * c.Y + plane.Z * c.Z + plane.W < -r;
    }

    /// <summary>
    ///     True if the sphere (<paramref name="center" />, <paramref name="radius" />) is at least
    ///     partially inside the frustum. Conservative: never reports a visible sphere as hidden.
    /// </summary>
    public bool IntersectsSphere(Vec3 center, float radius)
    {
        if (Outside(_left, center, radius)) return false;
        if (Outside(_right, center, radius)) return false;
        if (Outside(_bottom, center, radius)) return false;
        if (Outside(_top, center, radius)) return false;
        if (Outside(_near, center, radius)) return false;
        if (Outside(_far, center, radius)) return false;
        return true;
    }
}
