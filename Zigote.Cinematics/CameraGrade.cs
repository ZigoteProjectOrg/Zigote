using Zigote.Core.Native;

namespace Zigote.Cinematics;

/// <summary>
///     The resolved, per-frame output of <see cref="PhysicalCameraResolver.Resolve" /> — the values the
///     host writes to the renderer this frame. Projection goes through the camera-params FFI; the DoF /
///     exposure / grade knobs overwrite the corresponding fields of the global render settings (only when
///     the matching <c>Affect*</c> flag is set). The trailing native-effect fields are ignored until the
///     renderer's ABI carries them (native phase).
/// </summary>
public readonly struct CameraGrade
{
    // Projection
    public float FovYRadians { get; init; }
    public float NearM { get; init; }
    public float FarM { get; init; }

    // Depth of field
    public bool DofEnabled { get; init; }
    public float DofFocusDistance { get; init; }
    public float DofFStop { get; init; }
    public float DofMaxCoc { get; init; }

    // Exposure
    public float Ev100 { get; init; }
    public float Exposure { get; init; }

    // Film grade (existing tonemap knobs)
    public int Look { get; init; }
    public float Contrast { get; init; }
    public float Saturation { get; init; }
    public float WbTemperature { get; init; }
    public float WbTint { get; init; }
    public float Grain { get; init; }
    public float Vignette { get; init; }

    // Native-effect phase (Phase 7) — carried through but only applied once the ABI grows to hold them.
    public float DistortionK1 { get; init; }
    public float DistortionK2 { get; init; }
    public float MotionBlurShutter { get; init; }
    public float FocusBreathing { get; init; }
    public int ApertureBlades { get; init; }
    public float Anamorphic { get; init; }
    public int LutId { get; init; }
    public float LutStrength { get; init; }

    // Which knob groups the host should apply.
    public bool AffectExposure { get; init; }
    public bool AffectGrade { get; init; }
    public bool AffectDof { get; init; }

    /// <summary>
    ///     Merge this grade's owned knobs into the global render settings, leaving every other field
    ///     (sky/fog/SSAO/SSR/bloom/shadows/…) as the caller's base. Only the groups whose <c>Affect*</c>
    ///     flag is set are written. Projection (FOV/near/far) is applied separately through the
    ///     camera-params FFI, not here. The film-look knobs (AgX look / white balance / grain / vignette)
    ///     are written through the render settings now that the ABI carries them (Phase 4).
    /// </summary>
    public void ApplyTo(ref ZgRenderSettings3D s)
    {
        if (AffectDof)
        {
            s.DofEnabled = DofEnabled ? 1f : 0f;
            s.DofFocusDistance = DofFocusDistance;
            s.DofFStop = DofFStop;
            s.DofMaxCoc = DofMaxCoc;
        }

        if (AffectExposure)
            s.Exposure = Exposure;

        if (AffectGrade)
        {
            s.Contrast = Contrast;
            s.Saturation = Saturation;
            s.AgxLook = Look;
            s.WbTemperature = WbTemperature;
            s.WbTint = WbTint;
            s.VignetteStrength = Vignette;
            s.GrainAmount = Grain;
        }

        // Lens optics (distortion + aperture bokeh shape) are fundamental to the lens — applied whenever
        // the physical camera is active (this method is only invoked for an enabled physical camera).
        s.LensDistortionK1 = DistortionK1;
        s.LensDistortionK2 = DistortionK2;
        s.BokehBlades = ApertureBlades;
        s.BokehAnamorphic = Anamorphic;
    }
}
