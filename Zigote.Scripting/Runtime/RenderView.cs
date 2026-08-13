using Zigote.Core.Math3D;

namespace Zigote.Scripting;

/// <summary>
///     The camera/render state of the current frame, published by the host so scripts can do
///     view-dependent work (frustum culling, LOD selection, billboarding). Engine-generic and
///     host-set, mirroring <c>Hud</c>/<c>Physics</c>/<c>Instancing</c>. Outside play mode (or before
///     the host sets it) <see cref="IsAvailable" /> is false and the matrices are identity.
///     <see cref="ViewProjection" /> is the SAME proj*view the renderer draws with (column-major,
///     zero-to-one depth), so <c>Frustum.FromViewProjection(RenderView.ViewProjection)</c> culls
///     exactly what is on screen.
/// </summary>
public static class RenderView
{
    public static Mat4 ViewProjection { get; private set; } = Mat4.Identity;
    public static Vec3 CameraPosition { get; private set; } = Vec3.Zero;
    public static float ViewportWidth { get; private set; }
    public static float ViewportHeight { get; private set; }

    /// <summary>True once the host has published a camera for this run.</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>Aspect ratio (width/height); 1 when the viewport size is unknown.</summary>
    public static float Aspect => ViewportHeight > 0f ? ViewportWidth / ViewportHeight : 1f;

    /// <summary>Host: publish this frame's camera. Call before scripts run so culling is current.</summary>
    public static void Set(Mat4 viewProjection, Vec3 cameraPosition, float viewportWidth,
        float viewportHeight)
    {
        ViewProjection = viewProjection;
        CameraPosition = cameraPosition;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
        IsAvailable = true;
    }

    /// <summary>Host: publish just the viewport size (e.g. on resize) without a new camera.</summary>
    public static void SetViewport(float width, float height)
    {
        ViewportWidth = width;
        ViewportHeight = height;
    }

    /// <summary>Host: clear on stop so a stale camera doesn't linger.</summary>
    public static void Clear()
    {
        IsAvailable = false;
        ViewProjection = Mat4.Identity;
        CameraPosition = Vec3.Zero;
    }
}
