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
        float f = MathF.Max(x: focalLengthMm, y: 1e-3f);
        return 2f * MathF.Atan(sensorHeightMm / (2f * f));
    }

    /// <summary>
    ///     Focus breathing: the effective focal length grows slightly as focus shortens. Returns the
    ///     effective focal length (mm) for a lens focused at <paramref name="focusM" /> metres.
    /// </summary>
    public static float EffectiveFocalMm(float focalLengthMm, float focusM)
    {
        float f = MathF.Max(x: focalLengthMm, y: 1e-3f);
        float dMm = MathF.Max(x: focusM * 1000f, y: f + 1f); // keep denominator positive
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
        float f = MathF.Max(x: focalLengthMm, y: 1e-3f);
        float n = MathF.Max(x: fStop, y: 1e-3f);
        float s = MathF.Max(x: subjectM * 1000f, y: 1e-3f); // mm
        float d = MathF.Max(x: focusM * 1000f, y: f + 1e-3f); // mm, keep (d - f) > 0
        float aperture = f / n; // entrance-pupil diameter, mm
        return aperture * (f / (d - f)) * MathF.Abs(s - d) / s;
    }

    /// <summary>The background-blur ceiling (object at infinity) as a pixel radius, clamped for sanity.</summary>
    public static float BackgroundCocPixels(float focalLengthMm, float fStop, float focusM,
        float sensorHeightMm,
        float viewportHeightPx)
    {
        float f = MathF.Max(x: focalLengthMm, y: 1e-3f);
        float n = MathF.Max(x: fStop, y: 1e-3f);
        float d = MathF.Max(x: focusM * 1000f, y: f + 1e-3f);
        float aperture = f / n;
        float bgCocMm = aperture * (f / (d - f)); // |s-d|/s → 1 as s → ∞
        float px = bgCocMm / MathF.Max(x: sensorHeightMm, y: 1e-3f) * viewportHeightPx;
        return Math.Clamp(value: px, min: 0f, max: MaxCocPixelCap);
    }

    /// <summary>Exposure value at ISO 100 from the exposure triangle: EV100 = log2(N²/t) − log2(ISO/100).</summary>
    public static float Ev100(float fStop, float shutterSeconds, float iso)
    {
        float n = MathF.Max(x: fStop, y: 1e-3f);
        float t = MathF.Max(x: shutterSeconds, y: 1e-6f);
        float s = MathF.Max(x: iso, y: 1f);
        return MathF.Log2(n * n / t) - MathF.Log2(s / 100f);
    }

    /// <summary>Map an EV to the renderer's linear exposure multiplier — brighter EV → less digital gain.</summary>
    public static float ExposureMultiplier(float ev100)
    {
        float e = ReferenceExposure * MathF.Pow(x: 2f, y: ReferenceEv100 - ev100);
        return Math.Clamp(value: e, min: 0.1f, max: 4f);
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
        float sensorH = MathF.Max(x: cam.Sensor.HeightMm, y: 1e-3f);
        float focus = ResolveFocusDistance(
            cam: cam,
            subjectDistanceM: subjectDistanceM,
            dtSeconds: dtSeconds
        );
        cam.CurrentFocusDistanceM = focus;

        float fovY = VerticalFov(focalLengthMm: cam.Lens.FocalLengthMm, sensorHeightMm: sensorH);

        float ev = Ev100(
            fStop: cam.Lens.FStop,
            shutterSeconds: cam.Body.ShutterSpeed,
            iso: cam.Body.Iso
        );
        float exposure = ExposureMultiplier(ev);

        var film = cam.Film;
        float motionShutter = MotionBlurShutter(cam.Body);

        return new CameraGrade {
            FovYRadians = fovY,
            NearM = cam.NearM,
            FarM = cam.FarM,
            DofEnabled = cam.AffectDof,
            DofFocusDistance = focus,
            DofFStop = cam.Lens.FStop,
            DofMaxCoc = BackgroundCocPixels(
                focalLengthMm: cam.Lens.FocalLengthMm,
                fStop: cam.Lens.FStop,
                focusM: focus,
                sensorHeightMm: sensorH,
                viewportHeightPx: viewportHeightPx
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
                EffectiveFocalMm(focalLengthMm: cam.Lens.FocalLengthMm, focusM: focus) -
                cam.Lens.FocalLengthMm,
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
            return MathF.Max(x: cam.Focus.ManualDistanceM, y: 0.01f);

        float target = MathF.Max(x: subjectDistanceM, y: 0.01f);
        float current = cam.CurrentFocusDistanceM <= 0f ? target : cam.CurrentFocusDistanceM;
        float speed = cam.Focus.SpeedPerSec;
        if (speed <= 0f || dtSeconds <= 0f)
            return target;

        float alpha = 1f - MathF.Exp(-speed * dtSeconds);
        return current + ((target - current) * alpha);
    }

    // Shutter fraction 0..1 driving camera motion blur. Uses the cine shutter angle when set, else derives
    // an angle from the shutter time assuming a nominal 1/50 s reference frame.
    private static float MotionBlurShutter(CameraBody body)
    {
        if (body.ShutterAngleDeg > 0f)
            return Math.Clamp(value: body.ShutterAngleDeg / 360f, min: 0f, max: 1f);
        float reference = 1f / 50f;
        return Math.Clamp(value: body.ShutterSpeed / reference * 0.5f, min: 0f, max: 1f);
    }
}
