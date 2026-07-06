using Zigote.Core.Math3D;
using Zigote.Core.Physics;

namespace Zigote.Editor.Scene;

/// <summary>
///     Generates the line segments that outline a physics collision shape, for the editor's
///     "physics wireframe" debug overlay. Pure geometry (no rendering, no engine state) so it is
///     fully headless-testable; <see cref="Panels.ViewportPanel" /> projects the world-space edges
///     to screen and strokes them.
///     Shapes follow <see cref="PhysicsBodySettings" /> half-extent semantics (capsule/cylinder
///     axis = Y): Box = (hx,hy,hz); Sphere = radius in X; Capsule = (radius X, half-height Y);
///     Cylinder = (radius X, half-height Y).
/// </summary>
public static class PhysicsWireframe
{
    /// <summary>Segments per full circle for sphere/capsule/cylinder rings.</summary>
    public const int CircleSegments = 24;

    /// <summary>
    ///     Local-space (shape-centred at the origin) line segments outlining the collision shape.
    /// </summary>
    public static List<(Vec3 A, Vec3 B)> LocalEdges(PhysicsShapeType shape, Vec3 halfExtents)
    {
        var edges = new List<(Vec3, Vec3)>();
        switch (shape)
        {
            case PhysicsShapeType.Box:
                AddBox(edges, halfExtents);
                break;
            case PhysicsShapeType.Sphere:
                AddSphere(edges, MathF.Max(halfExtents.X, 1e-4f));
                break;
            case PhysicsShapeType.Capsule:
                AddCapsule(edges, MathF.Max(halfExtents.X, 1e-4f), MathF.Max(halfExtents.Y, 0f));
                break;
            case PhysicsShapeType.Cylinder:
                AddCylinder(edges, MathF.Max(halfExtents.X, 1e-4f), MathF.Max(halfExtents.Y, 0f));
                break;
        }

        return edges;
    }

    /// <summary>
    ///     World-space edges: the local outline rigidly transformed by (<paramref name="rotation" />,
    ///     <paramref name="position" />). Node scale is intentionally ignored — the physics body is
    ///     built from absolute half-extents, not the node's render scale.
    /// </summary>
    public static List<(Vec3 A, Vec3 B)> WorldEdges(
        PhysicsShapeType shape, Vec3 halfExtents, Vec3 position, Quat rotation)
    {
        var local = LocalEdges(shape, halfExtents);
        var world = new List<(Vec3, Vec3)>(local.Count);
        foreach (var (a, b) in local)
            world.Add((position + rotation.RotateVec(a), position + rotation.RotateVec(b)));
        return world;
    }

    private static void AddBox(List<(Vec3, Vec3)> edges, Vec3 h)
    {
        // 8 corners.
        var c = new Vec3[8];
        var i = 0;
        foreach (var sx in stackalloc[] {
                     -1f,
                     1f,
                 })
        foreach (var sy in stackalloc[] {
                     -1f,
                     1f,
                 })
        foreach (var sz in stackalloc[] {
                     -1f,
                     1f,
                 })
            c[i++] = new Vec3(sx * h.X, sy * h.Y, sz * h.Z);

        // Index layout: bit0=z, bit1=y, bit2=x. Connect corners differing in exactly one axis.
        for (var a = 0; a < 8; a++)
        for (var bit = 0; bit < 3; bit++)
        {
            var b = a | (1 << bit);
            if (b != a && (a & (1 << bit)) == 0) edges.Add((c[a], c[b]));
        }
    }

    private static void AddSphere(List<(Vec3, Vec3)> edges, float r)
    {
        AddRing(
            edges,
            Vec3.Zero,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            r
        ); // XY
        AddRing(
            edges,
            Vec3.Zero,
            new Vec3(1, 0, 0),
            new Vec3(0, 0, 1),
            r
        ); // XZ
        AddRing(
            edges,
            Vec3.Zero,
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1),
            r
        ); // YZ
    }

    private static void AddCylinder(List<(Vec3, Vec3)> edges, float r, float hh)
    {
        var top = new Vec3(0, hh, 0);
        var bottom = new Vec3(0, -hh, 0);
        AddRing(
            edges,
            top,
            new Vec3(1, 0, 0),
            new Vec3(0, 0, 1),
            r
        );
        AddRing(
            edges,
            bottom,
            new Vec3(1, 0, 0),
            new Vec3(0, 0, 1),
            r
        );
        // Four vertical connectors at +X, -X, +Z, -Z.
        edges.Add((new Vec3(r, -hh, 0), new Vec3(r, hh, 0)));
        edges.Add((new Vec3(-r, -hh, 0), new Vec3(-r, hh, 0)));
        edges.Add((new Vec3(0, -hh, r), new Vec3(0, hh, r)));
        edges.Add((new Vec3(0, -hh, -r), new Vec3(0, hh, -r)));
    }

    private static void AddCapsule(List<(Vec3, Vec3)> edges, float r, float hh)
    {
        var top = new Vec3(0, hh, 0);
        var bottom = new Vec3(0, -hh, 0);
        // Cap equator rings + four vertical connectors along the cylindrical body.
        AddRing(
            edges,
            top,
            new Vec3(1, 0, 0),
            new Vec3(0, 0, 1),
            r
        );
        AddRing(
            edges,
            bottom,
            new Vec3(1, 0, 0),
            new Vec3(0, 0, 1),
            r
        );
        edges.Add((new Vec3(r, -hh, 0), new Vec3(r, hh, 0)));
        edges.Add((new Vec3(-r, -hh, 0), new Vec3(-r, hh, 0)));
        edges.Add((new Vec3(0, -hh, r), new Vec3(0, hh, r)));
        edges.Add((new Vec3(0, -hh, -r), new Vec3(0, hh, -r)));

        // Hemispherical caps: an upper half-arc over the top centre and a lower half-arc under the
        // bottom centre, in both the XY and ZY planes (four arcs total).
        var half = CircleSegments / 2;
        AddArc(
            edges,
            top,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            r,
            0f,
            MathF.PI,
            half
        ); // top, XY
        AddArc(
            edges,
            top,
            new Vec3(0, 0, 1),
            new Vec3(0, 1, 0),
            r,
            0f,
            MathF.PI,
            half
        ); // top, ZY
        AddArc(
            edges,
            bottom,
            new Vec3(1, 0, 0),
            new Vec3(0, 1, 0),
            r,
            MathF.PI,
            MathF.Tau,
            half
        ); // bottom, XY
        AddArc(
            edges,
            bottom,
            new Vec3(0, 0, 1),
            new Vec3(0, 1, 0),
            r,
            MathF.PI,
            MathF.Tau,
            half
        ); // bottom, ZY
    }

    /// <summary>Append a closed ring (full circle) of <see cref="CircleSegments" /> segments.</summary>
    private static void AddRing(List<(Vec3, Vec3)> edges, Vec3 center, Vec3 u, Vec3 v, float r)
    {
        AddArc(
            edges,
            center,
            u,
            v,
            r,
            0f,
            MathF.Tau,
            CircleSegments
        );
    }

    /// <summary>Append an arc from <paramref name="a0" /> to <paramref name="a1" /> radians.</summary>
    private static void AddArc(List<(Vec3, Vec3)> edges, Vec3 center, Vec3 u, Vec3 v, float r,
        float a0, float a1, int segments)
    {
        Vec3 P(float t)
        {
            return center + u * (MathF.Cos(t) * r) + v * (MathF.Sin(t) * r);
        }

        var prev = P(a0);
        for (var i = 1; i <= segments; i++)
        {
            var t = a0 + (a1 - a0) * (i / (float)segments);
            var cur = P(t);
            edges.Add((prev, cur));
            prev = cur;
        }
    }
}