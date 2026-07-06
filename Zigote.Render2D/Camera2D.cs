using Zigote.Core.Math3D;

namespace Zigote.Render2D;

/// <summary>
///     World-space 2D camera: Y-up, X-right (the 3D world's XY plane); sprites live at their draw Z.
///     Produces a wgpu-ready view-projection (RhZo, depth 0..1) with near/far = -1000/1000.
/// </summary>
public sealed class Camera2D
{
    public float OrthoHeight = 10f;
    public Vec2 Position;
    public float Rotation;
    public float Zoom = 1f;

    public Mat4 ViewProjection(float viewportW, float viewportH)
    {
        var aspect = viewportH > 0f ? viewportW / viewportH : 1f;
        var zoom = MathF.Max(Zoom, 1e-6f);
        var halfH = OrthoHeight / zoom * 0.5f;
        var halfW = halfH * aspect;

        var proj = Mat4.OrthographicRhZo(
            -halfW,
            halfW,
            -halfH,
            halfH,
            -1000f,
            1000f
        );
        var view = Mat4.RotationZ(-Rotation) *
                   Mat4.Translation(new Vec3(-Position.X, -Position.Y, 0f));
        return proj * view;
    }

    /// <summary>Origin TOP-LEFT, +Y down, 1 unit = 1 px — the overlay-stage default (UI coords).</summary>
    public static Mat4 PixelOverlay(float viewportW, float viewportH)
    {
        return Mat4.OrthographicRhZo(
            0f,
            viewportW,
            viewportH,
            0f,
            -1000f,
            1000f
        );
    }
}