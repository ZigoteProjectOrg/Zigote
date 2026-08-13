using Zigote.Core;
using Zigote.Core.Math3D;

namespace Zigote.Scripting;

/// <summary>One queued world-space debug line segment.</summary>
public readonly struct DebugLine(Vec3 a, Vec3 b, Color color)
{
    public Vec3 A { get; } = a;
    public Vec3 B { get; } = b;
    public Color Color { get; } = color;
}

/// <summary>
///     Immediate-mode <b>3D</b> debug drawing for play mode: a game <see cref="Component" /> queues
///     world-space line segments each frame and the host (editor) projects + strokes them over the 3D
///     viewport. Generic — the host knows nothing about what the lines mean. Mirrors the
///     <see cref="Hud" /> (2D) and <see cref="Physics" /> static-provider pattern.
///     There is no native cost: the host already has the frame's view-projection (see
///     <see cref="RenderView" />). The editor only drains the queue while its physics-wireframe debug
///     overlay is on, and sets <see cref="Enabled" /> so games skip the trig when nothing will render.
/// </summary>
public static class DebugDraw
{
    internal static readonly List<DebugLine> Lines = [];

    /// <summary>The segments queued so far this frame (read-only; the host renders them).</summary>
    public static IReadOnlyList<DebugLine> Queue => Lines;

    /// <summary>
    ///     True when a host is rendering the queue this frame. Games gate their emission on this so
    ///     they do no work when the overlay is off. Host-set; defaults false (no consumer).
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>Host: clear the queue at the start of each frame, before scripts run.</summary>
    public static void BeginFrame()
    {
        Lines.Clear();
    }

    /// <summary>Host: drop the queue and disable on stop so nothing lingers.</summary>
    public static void Clear()
    {
        Lines.Clear();
        Enabled = false;
    }

    /// <summary>Queue a world-space line segment (no-op while <see cref="Enabled" /> is false).</summary>
    public static void Line(Vec3 a, Vec3 b, Color color)
    {
        if (!Enabled) return;
        if (!IsFinite(a) || !IsFinite(b)) return;
        Lines.Add(new DebugLine(a, b, color));
    }

    /// <summary>Queue a ray from <paramref name="origin" /> along <paramref name="dir" /> for a length.</summary>
    public static void Ray(Vec3 origin, Vec3 dir, float length, Color color)
    {
        Line(origin, origin + dir * length, color);
    }

    /// <summary>
    ///     Queue a circle of <paramref name="segments" /> segments centred at <paramref name="center" />
    ///     in the plane spanned by the (ideally unit, perpendicular) axes <paramref name="u" />/
    ///     <paramref name="v" />.
    /// </summary>
    public static void Circle(Vec3 center, Vec3 u, Vec3 v, float radius, Color color,
        int segments = 24)
    {
        if (!Enabled || radius <= 0f || segments < 3) return;
        var prev = center + u * radius;
        for (var i = 1; i <= segments; i++)
        {
            var t = MathF.Tau * (i / (float)segments);
            var cur = center + u * (MathF.Cos(t) * radius) + v * (MathF.Sin(t) * radius);
            Line(prev, cur, color);
            prev = cur;
        }
    }

    private static bool IsFinite(Vec3 v)
    {
        return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
