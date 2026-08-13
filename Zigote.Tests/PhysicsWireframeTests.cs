using Xunit;
using Zigote.Core.Math3D;
using Zigote.Core.Physics;
using Zigote.Editor.Scene;

namespace Zigote.Tests;

/// <summary>
///     The physics-wireframe overlay's geometry generator (<see cref="PhysicsWireframe" />). Pure
///     CPU geometry, so it's covered headlessly here even though the on-screen overlay itself needs
///     the viewport. Pins edge counts (so a regression in the shape tessellation is caught) and the
///     rigid world transform (translation applied, segment lengths preserved under rotation).
/// </summary>
public class PhysicsWireframeTests
{
    private const float Eps = 1e-4f;

    private static float Len((Vec3 A, Vec3 B) e)
    {
        var d = e.B - e.A;
        return MathF.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
    }

    [Fact]
    public void Box_Has12Edges_AllAxisAligned()
    {
        var h = new Vec3(x: 0.5f, y: 1f, z: 2f);
        var edges = PhysicsWireframe.LocalEdges(shape: PhysicsShapeType.Box, halfExtents: h);

        Assert.Equal(expected: 12, actual: edges.Count);

        // Every endpoint sits on a box corner (±h on each axis).
        foreach (var (a, b) in edges)
        foreach (var p in new[] {
                     a,
                     b,
                 })
        {
            Assert.Equal(expected: h.X, actual: MathF.Abs(p.X), precision: 3);
            Assert.Equal(expected: h.Y, actual: MathF.Abs(p.Y), precision: 3);
            Assert.Equal(expected: h.Z, actual: MathF.Abs(p.Z), precision: 3);
        }

        // Four edges of each axis length (2*half-extent).
        Assert.Equal(expected: 4, actual: edges.Count(e => MathF.Abs(Len(e) - (2f * h.X)) < Eps));
        Assert.Equal(expected: 4, actual: edges.Count(e => MathF.Abs(Len(e) - (2f * h.Y)) < Eps));
        Assert.Equal(expected: 4, actual: edges.Count(e => MathF.Abs(Len(e) - (2f * h.Z)) < Eps));
    }

    [Fact]
    public void Sphere_ThreeRings_OnRadius()
    {
        const float r = 1.5f;
        var edges = PhysicsWireframe.LocalEdges(
            shape: PhysicsShapeType.Sphere,
            halfExtents: new Vec3(x: r, y: 0f, z: 0f)
        );

        Assert.Equal(expected: 3 * PhysicsWireframe.CircleSegments, actual: edges.Count);
        foreach (var (a, b) in edges)
        foreach (var p in new[] {
                     a,
                     b,
                 })
        {
            Assert.Equal(
                expected: r,
                actual: MathF.Sqrt((p.X * p.X) + (p.Y * p.Y) + (p.Z * p.Z)),
                precision: 3
            );
        }
    }

    [Fact]
    public void Cylinder_RingsPlusConnectors()
    {
        var edges = PhysicsWireframe.LocalEdges(
            shape: PhysicsShapeType.Cylinder,
            halfExtents: new Vec3(x: 0.7f, y: 1.2f, z: 0f)
        );
        Assert.Equal(expected: (2 * PhysicsWireframe.CircleSegments) + 4, actual: edges.Count);
    }

    [Fact]
    public void Capsule_RingsConnectorsAndCaps()
    {
        var edges = PhysicsWireframe.LocalEdges(
            shape: PhysicsShapeType.Capsule,
            halfExtents: new Vec3(x: 0.5f, y: 1f, z: 0f)
        );
        // 2 equator rings + 4 vertical connectors + 4 hemispherical cap arcs (each half a circle).
        int expected = (2 * PhysicsWireframe.CircleSegments) + 4 +
                       (4 * (PhysicsWireframe.CircleSegments / 2));
        Assert.Equal(expected: expected, actual: edges.Count);
    }

    [Fact]
    public void WorldEdges_AppliesTranslation_PreservesCount()
    {
        var h = new Vec3(x: 0.5f, y: 0.5f, z: 0.5f);
        var pos = new Vec3(x: 10f, y: -3f, z: 4f);
        var local = PhysicsWireframe.LocalEdges(shape: PhysicsShapeType.Box, halfExtents: h);
        var world = PhysicsWireframe.WorldEdges(
            shape: PhysicsShapeType.Box,
            halfExtents: h,
            position: pos,
            rotation: Quat.Identity
        );

        Assert.Equal(expected: local.Count, actual: world.Count);
        for (int i = 0; i < local.Count; i++)
        {
            Assert.Equal(expected: local[i].A.X + pos.X, actual: world[i].A.X, precision: 3);
            Assert.Equal(expected: local[i].A.Y + pos.Y, actual: world[i].A.Y, precision: 3);
            Assert.Equal(expected: local[i].A.Z + pos.Z, actual: world[i].A.Z, precision: 3);
        }
    }

    [Fact]
    public void WorldEdges_RotationIsRigid_PreservesEdgeLengths()
    {
        var h = new Vec3(x: 0.5f, y: 1f, z: 2f);
        var rot = Quat.FromEuler(pitch: 0.3f, yaw: 1.1f, roll: -0.7f);
        var pos = new Vec3(x: 5f, y: 2f, z: -1f);
        var local = PhysicsWireframe.LocalEdges(shape: PhysicsShapeType.Box, halfExtents: h);
        var world = PhysicsWireframe.WorldEdges(
            shape: PhysicsShapeType.Box,
            halfExtents: h,
            position: pos,
            rotation: rot
        );

        for (int i = 0; i < local.Count; i++)
            Assert.Equal(expected: Len(local[i]), actual: Len(world[i]), precision: 3);
    }
}
