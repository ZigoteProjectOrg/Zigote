namespace Zigote.Cinematics;

/// <summary>
///     Pure optics / photometry. Turns an authored <see cref="PhysicalCamera" /> plus the frame's
///     subject distance and viewport height into a <see cref="CameraGrade" /> the host applies. All
///     methods are deterministic and side-effect-free except <see cref="Resolve" />, which advances
///     the
///     camera's autofocus state (<see cref="PhysicalCamera.CurrentFocusDistanceM" />).
/// </summary>
public static class PhysicalCameraResolver
{
    // Exposure mapping reference: a "normal" EV maps to the engine's default exposure multiplier.
    private const float ReferenceEv100 = 9.0f;
    private const float ReferenceExposure = 1.10f;
    private const float MaxCocPixelCap = 40f;

    /// <summary>Vertical field of view (radians) for a focal length on a sensor of the given gate height.</summary>
    public static float VerticalFov(float focalLengthMm, float sensorHeightMm)
    {
        var f = MathF.Max(focalLengthMm, 1e-3f);
        return 2f * MathF.Atan(sensorHeightMm / (2f * f));
    }

    /// <summary>
    ///     Focus breathing: the effective focal length grows slightly as focus shortens. Returns the
    ///     effective focal length (mm) for a lens focused at <paramref name="focusM" /> metres.
    /// </summary>
    public static float EffectiveFocalMm(float focalLengthMm, float focusM)
    {
        var f = MathF.Max(focalLengthMm, 1e-3f);
        var dMm = MathF.Max(focusM * 1000f, f + 1f); // keep denominator positive
        return f * (dMm / (dMm - f));
    }

    /// <summary>
    ///     Thin-lens circle of confusion on the sensor (mm) for an object at <paramref name="subjectM" />
    ///     metres when focused at <paramref name="focusM" />. Zero when the object is at the focus plane;
    ///     scales with aperture (1/f-stop) and with the relative depth error.
    /// </summary>
    public static float CircleOfConfusionMm(float focalLengthMm, float fStop, float subjectM,
        float focusM)
    {
        var f = MathF.Max(focalLengthMm, 1e-3f);
        var n = MathF.Max(fStop, 1e-3f);
        var s = MathF.Max(subjectM * 1000f, 1e-3f); // mm
        var d = MathF.Max(focusM * 1000f, f + 1e-3f); // mm, keep (d - f) > 0
        var aperture = f / n; // entrance-pupil diameter, mm
        return aperture * (f / (d - f)) * MathF.Abs(s - d) / s;
    }

    /// <summary>The background-blur ceiling (object at infinity) as a pixel radius, clamped for sanity.</summary>
    public static float BackgroundCocPixels(float focalLengthMm, float fStop, float focusM,
        float sensorHeightMm,
        float viewportHeightPx)
    {
        var f = MathF.Max(focalLengthMm, 1e-3f);
        var n = MathF.Max(fStop, 1e-3f);
        var d = MathF.Max(focusM * 1000f, f + 1e-3f);
        var aperture = f / n;
        var bgCocMm = aperture * (f / (d - f)); // |s-d|/s → 1 as s → ∞
        var px = bgCocMm / MathF.Max(sensorHeightMm, 1e-3f) * viewportHeightPx;
        return Math.Clamp(px, 0f, MaxCocPixelCap);
    }

    /// <summary>Exposure value at ISO 100 from the exposure triangle: EV100 = log2(N²/t) − log2(ISO/100).</summary>
    public static float Ev100(float fStop, float shutterSeconds, float iso)
    {
        var n = MathF.Max(fStop, 1e-3f);
        var t = MathF.Max(shutterSeconds, 1e-6f);
        var s = MathF.Max(iso, 1f);
        return MathF.Log2(n * n / t) - MathF.Log2(s / 100f);
    }

    /// <summary>Map an EV to the renderer's linear exposure multiplier — brighter EV → less digital gain.</summary>
    public static float ExposureMultiplier(float ev100)
    {
        var e = ReferenceExposure * MathF.Pow(2f, ReferenceEv100 - ev100);
        return Math.Clamp(e, 0.1f, 4f);
    }

    /// <summary>
    ///     Resolve the camera for this frame. <paramref name="subjectDistanceM" /> is the host-measured
    ///     distance for Center/Subject autofocus (ignored for Manual). Advances the camera's autofocus
    ///     distance by one <paramref name="dtSeconds" /> step.
    /// </summary>
    public static CameraGrade Resolve(PhysicalCamera cam, float subjectDistanceM,
        float viewportHeightPx,
        float dtSeconds)
    {
        var sensorH = MathF.Max(cam.Sensor.HeightMm, 1e-3f);
        var focus = ResolveFocusDistance(cam, subjectDistanceM, dtSeconds);
        cam.CurrentFocusDistanceM = focus;

        var fovY = VerticalFov(cam.Lens.FocalLengthMm, sensorH);

        var ev = Ev100(cam.Lens.FStop, cam.Body.ShutterSpeed, cam.Body.Iso);
        var exposure = ExposureMultiplier(ev);

        var film = cam.Film;
        var motionShutter = MotionBlurShutter(cam.Body);

        return new CameraGrade {
            FovYRadians = fovY,
            NearM = cam.NearM,
            FarM = cam.FarM,
            DofEnabled = cam.AffectDof,
            DofFocusDistance = focus,
            DofFStop = cam.Lens.FStop,
            DofMaxCoc = BackgroundCocPixels(
                cam.Lens.FocalLengthMm,
                cam.Lens.FStop,
                focus,
                sensorH,
                viewportHeightPx
            ),
            Ev100 = ev,
            Exposure = exposure,
            Look = film.Look,
            Contrast = film.Contrast,
            Saturation = film.Saturation,
            WbTemperature = film.WbTemperature,
            WbTint = film.WbTint,
            Grain = film.Grain,
            Vignette = film.Vignette,
            DistortionK1 = cam.Lens.DistortionK1,
            DistortionK2 = cam.Lens.DistortionK2,
            MotionBlurShutter = motionShutter,
            FocusBreathing =
                EffectiveFocalMm(cam.Lens.FocalLengthMm, focus) - cam.Lens.FocalLengthMm,
            ApertureBlades = cam.Lens.ApertureBlades,
            Anamorphic = cam.Lens.Anamorphic <= 0f ? 1f : cam.Lens.Anamorphic,
            LutId = film.LutId,
            LutStrength = film.Strength,
            AffectExposure = cam.AffectExposure,
            AffectGrade = cam.AffectGrade,
            AffectDof = cam.AffectDof,
        };
    }

    /// <summary>
    ///     Advance the autofocus distance one step (exponential approach; instant for Manual / speed
    ///     0).
    /// </summary>
    public static float ResolveFocusDistance(PhysicalCamera cam, float subjectDistanceM,
        float dtSeconds)
    {
        if (cam.Focus.Kind == FocusModeKind.Manual)
            return MathF.Max(cam.Focus.ManualDistanceM, 0.01f);

        var target = MathF.Max(subjectDistanceM, 0.01f);
        var current = cam.CurrentFocusDistanceM <= 0f ? target : cam.CurrentFocusDistanceM;
        var speed = cam.Focus.SpeedPerSec;
        if (speed <= 0f || dtSeconds <= 0f)
            return target;

        var alpha = 1f - MathF.Exp(-speed * dtSeconds);
        return current + (target - current) * alpha;
    }

    // Shutter fraction 0..1 driving camera motion blur. Uses the cine shutter angle when set, else derives
    // an angle from the shutter time assuming a nominal 1/50 s reference frame.
    private static float MotionBlurShutter(CameraBody body)
    {
        if (body.ShutterAngleDeg > 0f)
            return Math.Clamp(body.ShutterAngleDeg / 360f, 0f, 1f);
        var reference = 1f / 50f;
        return Math.Clamp(body.ShutterSpeed / reference * 0.5f, 0f, 1f);
    }
}