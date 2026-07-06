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
        return MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
    }

    [Fact]
    public void Box_Has12Edges_AllAxisAligned()
    {
        var h = new Vec3(0.5f, 1f, 2f);
        var edges = PhysicsWireframe.LocalEdges(PhysicsShapeType.Box, h);

        Assert.Equal(12, edges.Count);

        // Every endpoint sits on a box corner (±h on each axis).
        foreach (var (a, b) in edges)
        foreach (var p in new[] {
                     a,
                     b,
                 })
        {
            Assert.Equal(h.X, MathF.Abs(p.X), 3);
            Assert.Equal(h.Y, MathF.Abs(p.Y), 3);
            Assert.Equal(h.Z, MathF.Abs(p.Z), 3);
        }

        // Four edges of each axis length (2*half-extent).
        Assert.Equal(4, edges.Count(e => MathF.Abs(Len(e) - 2f * h.X) < Eps));
        Assert.Equal(4, edges.Count(e => MathF.Abs(Len(e) - 2f * h.Y) < Eps));
        Assert.Equal(4, edges.Count(e => MathF.Abs(Len(e) - 2f * h.Z) < Eps));
    }

    [Fact]
    public void Sphere_ThreeRings_OnRadius()
    {
        const float r = 1.5f;
        var edges = PhysicsWireframe.LocalEdges(PhysicsShapeType.Sphere, new Vec3(r, 0f, 0f));

        Assert.Equal(3 * PhysicsWireframe.CircleSegments, edges.Count);
        foreach (var (a, b) in edges)
        foreach (var p in new[] {
                     a,
                     b,
                 })
            Assert.Equal(r, MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z), 3);
    }

    [Fact]
    public void Cylinder_RingsPlusConnectors()
    {
        var edges = PhysicsWireframe.LocalEdges(
            PhysicsShapeType.Cylinder,
            new Vec3(0.7f, 1.2f, 0f)
        );
        Assert.Equal(2 * PhysicsWireframe.CircleSegments + 4, edges.Count);
    }

    [Fact]
    public void Capsule_RingsConnectorsAndCaps()
    {
        var edges = PhysicsWireframe.LocalEdges(PhysicsShapeType.Capsule, new Vec3(0.5f, 1f, 0f));
        // 2 equator rings + 4 vertical connectors + 4 hemispherical cap arcs (each half a circle).
        var expected = 2 * PhysicsWireframe.CircleSegments + 4 +
                       4 * (PhysicsWireframe.CircleSegments / 2);
        Assert.Equal(expected, edges.Count);
    }

    [Fact]
    public void WorldEdges_AppliesTranslation_PreservesCount()
    {
        var h = new Vec3(0.5f, 0.5f, 0.5f);
        var pos = new Vec3(10f, -3f, 4f);
        var local = PhysicsWireframe.LocalEdges(PhysicsShapeType.Box, h);
        var world = PhysicsWireframe.WorldEdges(
            PhysicsShapeType.Box,
            h,
            pos,
            Quat.Identity
        );

        Assert.Equal(local.Count, world.Count);
        for (var i = 0; i < local.Count; i++)
        {
            Assert.Equal(local[i].A.X + pos.X, world[i].A.X, 3);
            Assert.Equal(local[i].A.Y + pos.Y, world[i].A.Y, 3);
            Assert.Equal(local[i].A.Z + pos.Z, world[i].A.Z, 3);
        }
    }

    [Fact]
    public void WorldEdges_RotationIsRigid_PreservesEdgeLengths()
    {
        var h = new Vec3(0.5f, 1f, 2f);
        var rot = Quat.FromEuler(0.3f, 1.1f, -0.7f);
        var pos = new Vec3(5f, 2f, -1f);
        var local = PhysicsWireframe.LocalEdges(PhysicsShapeType.Box, h);
        var world = PhysicsWireframe.WorldEdges(
            PhysicsShapeType.Box,
            h,
            pos,
            rot
        );

        for (var i = 0; i < local.Count; i++)
            Assert.Equal(Len(local[i]), Len(world[i]), 3);
    }
}