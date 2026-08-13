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
                AddBox(edges: edges, h: halfExtents);
                break;
            case PhysicsShapeType.Sphere:
                AddSphere(edges: edges, r: MathF.Max(x: halfExtents.X, y: 1e-4f));
                break;
            case PhysicsShapeType.Capsule:
                AddCapsule(
                    edges: edges,
                    r: MathF.Max(x: halfExtents.X, y: 1e-4f),
                    hh: MathF.Max(x: halfExtents.Y, y: 0f)
                );
                break;
            case PhysicsShapeType.Cylinder:
                AddCylinder(
                    edges: edges,
                    r: MathF.Max(x: halfExtents.X, y: 1e-4f),
                    hh: MathF.Max(x: halfExtents.Y, y: 0f)
                );
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
        var local = LocalEdges(shape: shape, halfExtents: halfExtents);
        var world = new List<(Vec3, Vec3)>(local.Count);
        foreach (var (a, b) in local)
            world.Add((position + rotation.RotateVec(a), position + rotation.RotateVec(b)));
        return world;
    }

    private static void AddBox(List<(Vec3, Vec3)> edges, Vec3 h)
    {
        // 8 corners.
        var c = new Vec3[8];
        int i = 0;
        foreach (float sx in stackalloc[] {
                     -1f,
                     1f,
                 })
        foreach (float sy in stackalloc[] {
                     -1f,
                     1f,
                 })
        foreach (float sz in stackalloc[] {
                     -1f,
                     1f,
                 })
            c[i++] = new Vec3(x: sx * h.X, y: sy * h.Y, z: sz * h.Z);

        // Index layout: bit0=z, bit1=y, bit2=x. Connect corners differing in exactly one axis.
        for (int a = 0; a < 8; a++)
        for (int bit = 0; bit < 3; bit++)
        {
            int b = a | (1 << bit);
            if (b != a && (a & (1 << bit)) == 0) edges.Add((c[a], c[b]));
        }
    }

    private static void AddSphere(List<(Vec3, Vec3)> edges, float r)
    {
        AddRing(
            edges: edges,
            center: Vec3.Zero,
            u: new Vec3(x: 1, y: 0, z: 0),
            v: new Vec3(x: 0, y: 1, z: 0),
            r: r
        ); // XY
        AddRing(
            edges: edges,
            center: Vec3.Zero,
            u: new Vec3(x: 1, y: 0, z: 0),
            v: new Vec3(x: 0, y: 0, z: 1),
            r: r
        ); // XZ
        AddRing(
            edges: edges,
            center: Vec3.Zero,
            u: new Vec3(x: 0, y: 1, z: 0),
            v: new Vec3(x: 0, y: 0, z: 1),
            r: r
        ); // YZ
    }

    private static void AddCylinder(List<(Vec3, Vec3)> edges, float r, float hh)
    {
        var top = new Vec3(x: 0, y: hh, z: 0);
        var bottom = new Vec3(x: 0, y: -hh, z: 0);
        AddRing(
            edges: edges,
            center: top,
            u: new Vec3(x: 1, y: 0, z: 0),
            v: new Vec3(x: 0, y: 0, z: 1),
            r: r
        );
        AddRing(
            edges: edges,
            center: bottom,
            u: new Vec3(x: 1, y: 0, z: 0),
            v: new Vec3(x: 0, y: 0, z: 1),
            r: r
        );
        // Four vertical connectors at +X, -X, +Z, -Z.
        edges.Add((new Vec3(x: r, y: -hh, z: 0), new Vec3(x: r, y: hh, z: 0)));
        edges.Add((new Vec3(x: -r, y: -hh, z: 0), new Vec3(x: -r, y: hh, z: 0)));
        edges.Add((new Vec3(x: 0, y: -hh, z: r), new Vec3(x: 0, y: hh, z: r)));
        edges.Add((new Vec3(x: 0, y: -hh, z: -r), new Vec3(x: 0, y: hh, z: -r)));
    }

    private static void AddCapsule(List<(Vec3, Vec3)> edges, float r, float hh)
    {
        var top = new Vec3(x: 0, y: hh, z: 0);
        var bottom = new Vec3(x: 0, y: -hh, z: 0);
        // Cap equator rings + four vertical connectors along the cylindrical body.
        AddRing(
            edges: edges,
            center: top,
            u: new Vec3(x: 1, y: 0, z: 0),
            v: new Vec3(x: 0, y: 0, z: 1),
            r: r
        );
        AddRing(
            edges: edges,
            center: bottom,
            u: new Vec3(x: 1, y: 0, z: 0),
            v: new Vec3(x: 0, y: 0, z: 1),
            r: r
        );
        edges.Add((new Vec3(x: r, y: -hh, z: 0), new Vec3(x: r, y: hh, z: 0)));
        edges.Add((new Vec3(x: -r, y: -hh, z: 0), new Vec3(x: -r, y: hh, z: 0)));
        edges.Add((new Vec3(x: 0, y: -hh, z: r), new Vec3(x: 0, y: hh, z: r)));
        edges.Add((new Vec3(x: 0, y: -hh, z: -r), new Vec3(x: 0, y: hh, z: -r)));

        // Hemispherical caps: an upper half-arc over the top centre and a lower half-arc under the
        // bottom centre, in both the XY and ZY planes (four arcs total).
        int half = CircleSegments / 2;
        AddArc(
            edges: edges,
            center: top,
            u: new Vec3(x: 1, y: 0, z: 0),
            v: new Vec3(x: 0, y: 1, z: 0),
            r: r,
            a0: 0f,
            a1: MathF.PI,
            segments: half
        ); // top, XY
        AddArc(
            edges: edges,
            center: top,
            u: new Vec3(x: 0, y: 0, z: 1),
            v: new Vec3(x: 0, y: 1, z: 0),
            r: r,
            a0: 0f,
            a1: MathF.PI,
            segments: half
        ); // top, ZY
        AddArc(
            edges: edges,
            center: bottom,
            u: new Vec3(x: 1, y: 0, z: 0),
            v: new Vec3(x: 0, y: 1, z: 0),
            r: r,
            a0: MathF.PI,
            a1: MathF.Tau,
            segments: half
        ); // bottom, XY
        AddArc(
            edges: edges,
            center: bottom,
            u: new Vec3(x: 0, y: 0, z: 1),
            v: new Vec3(x: 0, y: 1, z: 0),
            r: r,
            a0: MathF.PI,
            a1: MathF.Tau,
            segments: half
        ); // bottom, ZY
    }

    /// <summary>Append a closed ring (full circle) of <see cref="CircleSegments" /> segments.</summary>
    private static void AddRing(List<(Vec3, Vec3)> edges, Vec3 center, Vec3 u, Vec3 v, float r)
    {
        AddArc(
            edges: edges,
            center: center,
            u: u,
            v: v,
            r: r,
            a0: 0f,
            a1: MathF.Tau,
            segments: CircleSegments
        );
    }

    /// <summary>Append an arc from <paramref name="a0" /> to <paramref name="a1" /> radians.</summary>
    private static void AddArc(List<(Vec3, Vec3)> edges, Vec3 center, Vec3 u, Vec3 v, float r,
        float a0, float a1, int segments)
    {
        Vec3 P(float t) => center + (u * (MathF.Cos(t) * r)) + (v * (MathF.Sin(t) * r));

        var prev = P(a0);
        for (int i = 1; i <= segments; i++)
        {
            float t = a0 + ((a1 - a0) * (i / (float)segments));
            var cur = P(t);
            edges.Add((prev, cur));
            prev = cur;
        }
    }
}
