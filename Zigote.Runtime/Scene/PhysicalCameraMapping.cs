using Zigote.Cinematics;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Bridges the editor's <see cref="SceneNode" /> camera fields to the pure
///     <see cref="Zigote.Cinematics.PhysicalCamera" /> model, keeping <c>Zigote.Cinematics</c> free of
///     any
///     <see cref="SceneNode" /> dependency. Use <see cref="Apply" /> to refresh a cached camera each
///     frame
///     so its autofocus state (<see cref="PhysicalCamera.CurrentFocusDistanceM" />) survives.
/// </summary>
public static class PhysicalCameraMapping
{
    /// <summary>Build a fresh <see cref="PhysicalCamera" /> from a camera node's authored fields.</summary>
    public static PhysicalCamera ToPhysicalCamera(SceneNode node)
    {
        var pc = new PhysicalCamera();
        Apply(node, pc);
        return pc;
    }

    /// <summary>Copy the node's authored fields onto an existing camera, preserving its autofocus state.</summary>
    public static void Apply(SceneNode node, PhysicalCamera into)
    {
        var preset = (SensorPreset)node.PhysSensorPreset;
        into.Enabled = node.PhysEnabled;
        into.SensorPreset = preset;
        into.Sensor = preset == SensorPreset.Custom
            ? new SensorFormat {
                WidthMm = node.PhysSensorWidthMm,
                HeightMm = node.PhysSensorHeightMm,
            }
            : SensorFormat.Of(preset);
        into.Lens = new Lens {
            FocalLengthMm = node.PhysFocalLengthMm,
            FStop = node.PhysFStop,
            ApertureBlades = node.PhysApertureBlades,
            Anamorphic = node.PhysAnamorphic <= 0f ? 1f : node.PhysAnamorphic,
            DistortionK1 = node.PhysDistortionK1,
        };
        into.Body = new CameraBody {
            Iso = node.PhysIso,
            ShutterSpeed = node.PhysShutterSpeed,
        };
        into.Focus = new FocusSettings {
            Kind = (FocusModeKind)node.PhysFocusMode,
            ManualDistanceM = node.PhysManualFocusM,
            SpeedPerSec = node.PhysFocusSpeed,
        };
        into.Film = FilmStock.Of((FilmStockKind)node.PhysFilmStock, node.PhysFilmStrength);
        into.NearM = node.CameraNear;
        into.FarM = node.CameraFar;
        into.AffectExposure = node.PhysAffectExposure;
        into.AffectGrade = node.PhysAffectGrade;
        into.AffectDof = node.PhysAffectDof;
    }
}
