namespace Zigote.Cinematics;

/// <summary>
///     The authored physical-camera configuration for one camera. Mutable so the editor inspector and
///     the runtime scripting API can bind to it directly; <see cref="PhysicalCameraResolver.Resolve" />
///     turns it into a per-frame <see cref="CameraGrade" /> the host applies to the renderer.
/// </summary>
public sealed class PhysicalCamera
{
    /// <summary>Master switch: when false the camera falls back to its plain FOV and global render settings.</summary>
    public bool Enabled { get; set; }

    public SensorPreset SensorPreset { get; set; } = SensorPreset.FullFrame;
    public SensorFormat Sensor { get; set; } = SensorFormat.Of(SensorPreset.FullFrame);
    public Lens Lens { get; set; } = Lens.Default;
    public CameraBody Body { get; set; } = CameraBody.Default;
    public FocusSettings Focus { get; set; } = FocusSettings.Default;
    public FilmStock Film { get; set; } = FilmStock.Of(FilmStockKind.Neutral, 1f);

    public float NearM { get; set; } = 0.1f;
    public float FarM { get; set; } = 4000f;

    /// <summary>ISO/shutter/aperture drives the exposure multiplier.</summary>
    public bool AffectExposure { get; set; } = true;

    /// <summary>Film stock drives the AgX look / white balance / saturation / contrast / grain / vignette.</summary>
    public bool AffectGrade { get; set; } = true;

    /// <summary>Physical DoF drives the depth-of-field settings.</summary>
    public bool AffectDof { get; set; } = true;

    /// <summary>Live autofocus distance in metres (mutated by <see cref="PhysicalCameraResolver.Resolve" />; not serialized).</summary>
    public float CurrentFocusDistanceM { get; set; }
}