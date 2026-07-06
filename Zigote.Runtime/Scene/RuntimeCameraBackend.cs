using Zigote.Cinematics;
using Zigote.Scripting;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Backs the generic <see cref="Camera" /> scripting API in play mode by writing the active
///     camera node's physical-camera fields. <see cref="GameSession.PublishRenderView" /> then
///     resolves
///     them into the render each frame (FOV / depth of field / exposure / film grade), and
///     <see cref="SceneNode.SyncToNative" /> pushes the FOV. Any lens control enables the physical
///     camera.
/// </summary>
internal sealed class RuntimeCameraBackend(SceneNode root) : ICameraBackend
{
    private SceneNode? ActiveCamera => FindCamera(root);

    public void SetPhysicalEnabled(bool enabled)
    {
        WithCamera(c => c.PhysEnabled = enabled, false);
    }

    public void SetFocalLength(float millimetres)
    {
        WithCamera(c => c.PhysFocalLengthMm = millimetres);
    }

    public void SetSensor(SensorPreset preset)
    {
        WithCamera(c => c.PhysSensorPreset = (int)preset);
    }

    public void SetSensorSize(float widthMm, float heightMm)
    {
        WithCamera(c =>
            {
                c.PhysSensorPreset = (int)SensorPreset.Custom;
                c.PhysSensorWidthMm = widthMm;
                c.PhysSensorHeightMm = heightMm;
            }
        );
    }

    public void SetAperture(float fStop)
    {
        WithCamera(c => c.PhysFStop = fStop);
    }

    public void SetIso(float iso)
    {
        WithCamera(c => c.PhysIso = iso);
    }

    public void SetShutter(float seconds)
    {
        WithCamera(c => c.PhysShutterSpeed = seconds);
    }

    public void SetFocusMode(FocusModeKind mode)
    {
        WithCamera(c => c.PhysFocusMode = (int)mode, false);
    }

    public void SetManualFocus(float metres)
    {
        WithCamera(c => c.PhysManualFocusM = metres, false);
    }

    public void SetFilmStock(FilmStockKind stock, float strength)
    {
        WithCamera(
            c =>
            {
                c.PhysFilmStock = (int)stock;
                c.PhysFilmStrength = strength;
            },
            false
        );
    }

    private void WithCamera(Action<SceneNode> apply, bool enable = true)
    {
        if (ActiveCamera is not { } c) return;
        if (enable) c.PhysEnabled = true; // a lens/exposure control turns the physical camera on
        apply(c);
    }

    private static SceneNode? FindCamera(SceneNode node)
    {
        if (node.Kind == NodeKind.Camera) return node;
        foreach (var child in node.Children)
        {
            var found = FindCamera(child);
            if (found != null) return found;
        }

        return null;
    }
}