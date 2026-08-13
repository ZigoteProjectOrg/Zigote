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
            x: vp.Get(col: 0, row: 0),
            y: vp.Get(col: 1, row: 0),
            z: vp.Get(col: 2, row: 0),
            w: vp.Get(col: 3, row: 0)
        );
        var r1 = new Vec4(
            x: vp.Get(col: 0, row: 1),
            y: vp.Get(col: 1, row: 1),
            z: vp.Get(col: 2, row: 1),
            w: vp.Get(col: 3, row: 1)
        );
        var r2 = new Vec4(
            x: vp.Get(col: 0, row: 2),
            y: vp.Get(col: 1, row: 2),
            z: vp.Get(col: 2, row: 2),
            w: vp.Get(col: 3, row: 2)
        );
        var r3 = new Vec4(
            x: vp.Get(col: 0, row: 3),
            y: vp.Get(col: 1, row: 3),
            z: vp.Get(col: 2, row: 3),
            w: vp.Get(col: 3, row: 3)
        );

        // Gribb–Hartmann for [0,1] clip-z: side planes from w±axis, near = z, far = w − z.
        return new Frustum(
            left: Normalize(r3 + r0), // left
            right: Normalize(r3 - r0), // right
            bottom: Normalize(r3 + r1), // bottom
            top: Normalize(r3 - r1), // top
            near: Normalize(r2), // near
            far: Normalize(r3 - r2)
        ); // far
    }

    private static Vec4 Normalize(Vec4 p)
    {
        float len = MathF.Sqrt((p.X * p.X) + (p.Y * p.Y) + (p.Z * p.Z));
        if (len < 1e-20f) return p;
        float inv = 1f / len;
        return new Vec4(
            x: p.X * inv,
            y: p.Y * inv,
            z: p.Z * inv,
            w: p.W * inv
        );
    }

    private static bool Outside(Vec4 plane, Vec3 c, float r) =>
        (plane.X * c.X) + (plane.Y * c.Y) + (plane.Z * c.Z) + plane.W < -r;

    /// <summary>
    ///     True if the sphere (<paramref name="center" />, <paramref name="radius" />) is at least
    ///     partially inside the frustum. Conservative: never reports a visible sphere as hidden.
    /// </summary>
    public bool IntersectsSphere(Vec3 center, float radius)
    {
        if (Outside(plane: _left, c: center, r: radius)) return false;
        if (Outside(plane: _right, c: center, r: radius)) return false;
        if (Outside(plane: _bottom, c: center, r: radius)) return false;
        if (Outside(plane: _top, c: center, r: radius)) return false;
        if (Outside(plane: _near, c: center, r: radius)) return false;
        if (Outside(plane: _far, c: center, r: radius)) return false;
        return true;
    }
}
