using Zigote.Cinematics;
using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Core.Native;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Drives the active camera's physical-camera model each frame: resolves the photographic grade
///     and
///     overlays it onto the global render settings, snapshotting the pre-physical (SettingsPanel)
///     values so
///     they are restored when the physical camera is disabled. Shared by edit-mode preview
///     (<c>ViewportPanel</c>) and play (<c>GameSession</c>) so both show the same look. FOV is pushed
///     separately (<see cref="SceneNode.SyncToNative" /> → <c>SceneSetCameraParams</c>); the resolved
///     grade
///     is returned so a caller that publishes a culling frustum can use the matching FOV.
/// </summary>
public sealed class PhysicalCameraDriver
{
    private readonly PhysicalCamera _cam = new();
    private bool _active;
    private ZgRenderSettings3D _saved;

    /// <summary>
    ///     Resolve <paramref name="camera" /> (if it is a physical camera) and apply its grade to the
    ///     global
    ///     render settings, returning the resolved grade. Returns null and restores the settings when the
    ///     camera is null or not physical. <paramref name="cameraWorldPos" /> is the position the frame is
    ///     rendered from (orbit camera in edit, the camera node in play) — used for subject autofocus.
    /// </summary>
    public CameraGrade? Apply(SceneNode? camera, Vec3 cameraWorldPos, float viewportHeightPx,
        float dt,
        Func<int, SceneNode?> findNode)
    {
        var engine = ZigoteEngine.Instance;
        if (engine == null || camera is not { PhysEnabled: true })
        {
            Restore();
            return null;
        }

        PhysicalCameraMapping.Apply(node: camera, into: _cam);
        float subjectDist = SubjectDistance(
            camera: camera,
            cameraWorldPos: cameraWorldPos,
            findNode: findNode
        );
        var grade = PhysicalCameraResolver.Resolve(
            cam: _cam,
            subjectDistanceM: subjectDist,
            viewportHeightPx: viewportHeightPx,
            dtSeconds: dt
        );

        var s = engine.GetRenderSettings3D();
        if (!_active)
        {
            _saved = s; // first activation: remember the SettingsPanel values to restore later
            _active = true;
        }

        grade.ApplyTo(ref s);
        engine.SetRenderSettings3D(s);
        return grade;
    }

    /// <summary>Return the knobs the physical camera owns to their pre-physical (SettingsPanel) values.</summary>
    public void Restore()
    {
        if (!_active) return;
        _active = false;

        var engine = ZigoteEngine.Instance;
        if (engine == null) return;

        var s = engine.GetRenderSettings3D();
        s.DofEnabled = _saved.DofEnabled;
        s.DofFocusDistance = _saved.DofFocusDistance;
        s.DofFStop = _saved.DofFStop;
        s.DofMaxCoc = _saved.DofMaxCoc;
        s.Exposure = _saved.Exposure;
        s.Contrast = _saved.Contrast;
        s.Saturation = _saved.Saturation;
        s.AgxLook = _saved.AgxLook;
        s.WbTemperature = _saved.WbTemperature;
        s.WbTint = _saved.WbTint;
        s.VignetteStrength = _saved.VignetteStrength;
        s.GrainAmount = _saved.GrainAmount;
        s.LensDistortionK1 = _saved.LensDistortionK1;
        s.LensDistortionK2 = _saved.LensDistortionK2;
        s.BokehBlades = _saved.BokehBlades;
        s.BokehAnamorphic = _saved.BokehAnamorphic;
        engine.SetRenderSettings3D(s);
    }

    private float SubjectDistance(SceneNode camera, Vec3 cameraWorldPos,
        Func<int, SceneNode?> findNode)
    {
        // Subject mode tracks a target node; Center seeds the value (the DoF shader does the true centre-spot
        // autofocus) and Manual ignores it (the resolver uses the manual distance).
        if (camera.PhysFocusMode == (int)FocusModeKind.Subject &&
            camera.PhysFocusTargetNodeId is int id)
        {
            var target = findNode(id);
            if (target != null)
            {
                var diff = WorldPosition(target) - cameraWorldPos;
                return MathF.Max(x: diff.Length(), y: 0.01f);
            }
        }

        return _cam.CurrentFocusDistanceM > 0f
            ? _cam.CurrentFocusDistanceM
            : camera.PhysManualFocusM;
    }

    private static Vec3 WorldPosition(SceneNode node)
    {
        var t = new Transform3D(
            position: node.Position,
            rotation: node.Rotation,
            scale: node.Scale
        );
        for (var p = node.Parent; p != null; p = p.Parent)
        {
            t = Transform3D.Combine(
                parent: new Transform3D(position: p.Position, rotation: p.Rotation, scale: p.Scale),
                child: t
            );
        }

        return t.Position;
    }
}
