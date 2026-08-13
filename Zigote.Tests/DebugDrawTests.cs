using Xunit;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Scripting;

namespace Zigote.Tests;

/// <summary>
///     The generic 3D <see cref="DebugDraw" /> queue (game emits world-space lines; host renders
///     them).
///     Only this test class touches the static, so its sequential-within-class runs are isolated.
/// </summary>
public class DebugDrawTests
{
    private static readonly Color C = new(r: 1f, g: 1f, b: 1f);

    [Fact]
    public void Disabled_QueuesNothing()
    {
        DebugDraw.Clear(); // Enabled = false
        DebugDraw.Line(a: Vec3.Zero, b: Vec3.One, color: C);
        DebugDraw.Circle(
            center: Vec3.Zero,
            u: Vec3.Right,
            v: Vec3.Up,
            radius: 1f,
            color: C
        );
        Assert.Empty(DebugDraw.Queue);
    }

    [Fact]
    public void Enabled_Line_Queues()
    {
        DebugDraw.BeginFrame();
        DebugDraw.Enabled = true;
        DebugDraw.Line(a: Vec3.Zero, b: new Vec3(x: 1, y: 2, z: 3), color: C);
        Assert.Single(DebugDraw.Queue);
        Assert.Equal(expected: 3f, actual: DebugDraw.Queue[0].B.Z, precision: 3);
        DebugDraw.Clear();
    }

    [Fact]
    public void Circle_Emits_OneSegmentPerStep()
    {
        DebugDraw.BeginFrame();
        DebugDraw.Enabled = true;
        DebugDraw.Circle(
            center: Vec3.Zero,
            u: Vec3.Right,
            v: Vec3.Up,
            radius: 2f,
            color: C,
            segments: 16
        );
        Assert.Equal(expected: 16, actual: DebugDraw.Queue.Count);
        // Closed loop: last segment ends back at the start point (radius along +X).
        Assert.Equal(expected: 2f, actual: DebugDraw.Queue[^1].B.X, precision: 3);
        Assert.Equal(expected: 0f, actual: DebugDraw.Queue[^1].B.Y, precision: 3);
        DebugDraw.Clear();
    }

    [Fact]
    public void BeginFrame_ClearsQueue_KeepsEnabled()
    {
        DebugDraw.BeginFrame();
        DebugDraw.Enabled = true;
        DebugDraw.Line(a: Vec3.Zero, b: Vec3.One, color: C);
        DebugDraw.BeginFrame();
        Assert.Empty(DebugDraw.Queue);
        Assert.True(DebugDraw.Enabled);
        DebugDraw.Clear();
    }

    [Fact]
    public void NaN_Endpoints_Skipped()
    {
        DebugDraw.BeginFrame();
        DebugDraw.Enabled = true;
        DebugDraw.Line(a: Vec3.Zero, b: new Vec3(x: float.NaN, y: 0, z: 0), color: C);
        Assert.Empty(DebugDraw.Queue);
        DebugDraw.Clear();
    }
}
