namespace Zigote.Core.Math3D;

public readonly struct Ray(Vec3 origin, Vec3 direction)
{
    public Vec3 Origin { get; } = origin;

    /// <summary>Should be normalised.</summary>
    public Vec3 Direction { get; } = direction;

    public Vec3 At(float t)
    {
        return Origin + Direction * t;
    }

    /// <summary>Möller–Trumbore intersection test. Returns t > 0 if hit, else null.</summary>
    public float? IntersectTriangle(Vec3 v0, Vec3 v1, Vec3 v2)
    {
        const float eps = 1e-7f;
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = Direction.Cross(edge2);
        var a = edge1.Dot(h);
        if (a is > -eps and < eps) return null;
        var f = 1f / a;
        var s = Origin - v0;
        var u = f * s.Dot(h);
        if (u < 0f || u > 1f) return null;
        var q = s.Cross(edge1);
        var v = f * Direction.Dot(q);
        if (v < 0f || u + v > 1f) return null;
        var t = f * edge2.Dot(q);
        return t > eps ? t : null;
    }

    /// <summary>AABB slab intersection. Returns (tmin, tmax) or null if miss.</summary>
    public (float tmin, float tmax)? IntersectAabb(Vec3 aabbMin, Vec3 aabbMax)
    {
        var invDir = new Vec3(1f / Direction.X, 1f / Direction.Y, 1f / Direction.Z);
        var t1 = (aabbMin.X - Origin.X) * invDir.X;
        var t2 = (aabbMax.X - Origin.X) * invDir.X;
        var t3 = (aabbMin.Y - Origin.Y) * invDir.Y;
        var t4 = (aabbMax.Y - Origin.Y) * invDir.Y;
        var t5 = (aabbMin.Z - Origin.Z) * invDir.Z;
        var t6 = (aabbMax.Z - Origin.Z) * invDir.Z;
        var tmin = MathF.Max(MathF.Max(MathF.Min(t1, t2), MathF.Min(t3, t4)), MathF.Min(t5, t6));
        var tmax = MathF.Min(MathF.Min(MathF.Max(t1, t2), MathF.Max(t3, t4)), MathF.Max(t5, t6));
        if (tmax < 0f || tmin > tmax) return null;
        return (tmin, tmax);
    }

    public override string ToString()
    {
        return $"Ray(origin={Origin}, dir={Direction})";
    }
}
