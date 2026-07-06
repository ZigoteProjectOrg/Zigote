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
    private static readonly Color C = new(1f, 1f, 1f);

    [Fact]
    public void Disabled_QueuesNothing()
    {
        DebugDraw.Clear(); // Enabled = false
        DebugDraw.Line(Vec3.Zero, Vec3.One, C);
        DebugDraw.Circle(
            Vec3.Zero,
            Vec3.Right,
            Vec3.Up,
            1f,
            C
        );
        Assert.Empty(DebugDraw.Queue);
    }

    [Fact]
    public void Enabled_Line_Queues()
    {
        DebugDraw.BeginFrame();
        DebugDraw.Enabled = true;
        DebugDraw.Line(Vec3.Zero, new Vec3(1, 2, 3), C);
        Assert.Single(DebugDraw.Queue);
        Assert.Equal(3f, DebugDraw.Queue[0].B.Z, 3);
        DebugDraw.Clear();
    }

    [Fact]
    public void Circle_Emits_OneSegmentPerStep()
    {
        DebugDraw.BeginFrame();
        DebugDraw.Enabled = true;
        DebugDraw.Circle(
            Vec3.Zero,
            Vec3.Right,
            Vec3.Up,
            2f,
            C,
            16
        );
        Assert.Equal(16, DebugDraw.Queue.Count);
        // Closed loop: last segment ends back at the start point (radius along +X).
        Assert.Equal(2f, DebugDraw.Queue[^1].B.X, 3);
        Assert.Equal(0f, DebugDraw.Queue[^1].B.Y, 3);
        DebugDraw.Clear();
    }

    [Fact]
    public void BeginFrame_ClearsQueue_KeepsEnabled()
    {
        DebugDraw.BeginFrame();
        DebugDraw.Enabled = true;
        DebugDraw.Line(Vec3.Zero, Vec3.One, C);
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
        DebugDraw.Line(Vec3.Zero, new Vec3(float.NaN, 0, 0), C);
        Assert.Empty(DebugDraw.Queue);
        DebugDraw.Clear();
    }
}