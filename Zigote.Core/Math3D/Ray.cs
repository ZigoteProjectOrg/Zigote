namespace Zigote.Core.Math3D;

public readonly struct Ray(Vec3 origin, Vec3 direction)
{
    public Vec3 Origin { get; } = origin;

    /// <summary>Should be normalised.</summary>
    public Vec3 Direction { get; } = direction;

    public Vec3 At(float t) => Origin + (Direction * t);

    /// <summary>Möller–Trumbore intersection test. Returns t > 0 if hit, else null.</summary>
    public float? IntersectTriangle(Vec3 v0, Vec3 v1, Vec3 v2)
    {
        const float eps = 1e-7f;
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = Direction.Cross(edge2);
        float a = edge1.Dot(h);
        if (a is > -eps and < eps) return null;
        float f = 1f / a;
        var s = Origin - v0;
        float u = f * s.Dot(h);
        if (u < 0f || u > 1f) return null;
        var q = s.Cross(edge1);
        float v = f * Direction.Dot(q);
        if (v < 0f || u + v > 1f) return null;
        float t = f * edge2.Dot(q);
        return t > eps ? t : null;
    }

    /// <summary>AABB slab intersection. Returns (tmin, tmax) or null if miss.</summary>
    public (float tmin, float tmax)? IntersectAabb(Vec3 aabbMin, Vec3 aabbMax)
    {
        var invDir = new Vec3(x: 1f / Direction.X, y: 1f / Direction.Y, z: 1f / Direction.Z);
        float t1 = (aabbMin.X - Origin.X) * invDir.X;
        float t2 = (aabbMax.X - Origin.X) * invDir.X;
        float t3 = (aabbMin.Y - Origin.Y) * invDir.Y;
        float t4 = (aabbMax.Y - Origin.Y) * invDir.Y;
        float t5 = (aabbMin.Z - Origin.Z) * invDir.Z;
        float t6 = (aabbMax.Z - Origin.Z) * invDir.Z;
        float tmin = MathF.Max(
            x: MathF.Max(x: MathF.Min(x: t1, y: t2), y: MathF.Min(x: t3, y: t4)),
            y: MathF.Min(x: t5, y: t6)
        );
        float tmax = MathF.Min(
            x: MathF.Min(x: MathF.Max(x: t1, y: t2), y: MathF.Max(x: t3, y: t4)),
            y: MathF.Max(x: t5, y: t6)
        );
        if (tmax < 0f || tmin > tmax) return null;
        return (tmin, tmax);
    }

    public override string ToString() => $"Ray(origin={Origin}, dir={Direction})";
}
