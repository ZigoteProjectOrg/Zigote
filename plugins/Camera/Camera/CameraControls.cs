using Zigote.Core;
using Zigote.Core.State;

namespace Camera;

/// <summary>
///     Which exposure parameters the photographer has taken over. Derived, not chosen: a UI sets
///     ISO and/or shutter and reads back the mode that implies, so there is no mode switch to keep
///     in sync with the dials.
/// </summary>
public enum ExposureMode
{
    /// <summary>The device meters everything; only EV compensation applies.</summary>
    Auto,

    /// <summary>ISO is fixed, the device picks the shutter.</summary>
    IsoPriority,

    /// <summary>Shutter is fixed, the device picks the ISO.</summary>
    ShutterPriority,

    /// <summary>Both fixed; metering is off.</summary>
    Manual,
}

/// <summary>
///     A DNG straight off the sensor, plus what the sensor was doing when it was taken. The
///     bytes are a complete, standard file — every raw developer opens it, and the app never has
///     to understand a vendor's idea of raw.
/// </summary>
/// <param name="Dng">A complete DNG file, ready to write to disk.</param>
/// <param name="Metadata">The exposure this frame was shot at, for the readout and the recipe.</param>
public sealed record RawPhoto(byte[] Dng, CaptureMetadata? Metadata);

/// <summary>
///     A still image format. JPEG is the floor every platform can write; the others depend on
///     what the device has, which is why <see cref="CameraPlugin.SupportsFormat" /> exists rather
///     than a table of assumptions.
/// </summary>
public enum StillFormat
{
    /// <summary>Universal. Lossy, 8-bit, and understood by everything.</summary>
    Jpeg,

    /// <summary>Lossless and universally readable, at several times the size.</summary>
    Png,

    /// <summary>
    ///     JPEG XL: the best quality per byte of the three, and lossless if asked. Needs an
    ///     encoder that is not present everywhere — check before offering it.
    /// </summary>
    JpegXl,
}

/// <summary>An inclusive ISO range. <see cref="Supported" /> is false when the device has no manual ISO.</summary>
public readonly record struct IsoRange(int Min, int Max)
{
    public bool Supported => Max > Min && Min > 0;

    public int Clamp(int iso) => Supported ? Math.Clamp(value: iso, min: Min, max: Max) : iso;
}

/// <summary>An inclusive exposure-time range in nanoseconds.</summary>
public readonly record struct ShutterRange(long MinNs, long MaxNs)
{
    public bool Supported => MaxNs > MinNs && MinNs > 0;

    public long Clamp(long ns) => Supported ? Math.Clamp(value: ns, min: MinNs, max: MaxNs) : ns;
}

/// <summary>
///     What one camera — one <em>lens</em>, since an ultra-wide routinely offers less than the
///     main sensor on the same phone — can actually do. Probed once when the session opens and
///     published on <see cref="CameraController.Capabilities" />.
///     <para>
///         This record is what a UI builds itself from: a control whose range is unsupported is
///         not rendered at all, rather than rendered and disabled. A greyed-out dial that some
///         other setting silently switched off is the single most complained-about behaviour in
///         the pro camera apps this plugin exists to serve.
///     </para>
/// </summary>
/// <param name="Iso">Manual sensitivity range, or <see cref="IsoRange.Supported" /> false.</param>
/// <param name="Shutter">Manual exposure-time range, or unsupported.</param>
/// <param name="EvStep">EV compensation step size, in stops (0 when unsupported).</param>
/// <param name="EvRange">EV compensation bounds, in steps of <paramref name="EvStep" />.</param>
/// <param name="Kelvin">Manual white-balance temperature bounds, or (0, 0).</param>
/// <param name="Tint">Whether white-balance tint is separately adjustable.</param>
/// <param name="MinFocusDiopters">Closest focus, in diopters (1/m). 0 when focus is fixed.</param>
/// <param name="ManualFocus">Whether the lens position can be driven directly.</param>
/// <param name="OisToggle">Whether optical stabilization can be turned off (it often cannot).</param>
/// <param name="Regions">Whether AE/AF metering regions can be set (tap to meter).</param>
/// <param name="Raw">Whether a DNG still can be captured.</param>
public sealed record CameraCapabilities(
    IsoRange Iso,
    ShutterRange Shutter,
    float EvStep,
    (int Min, int Max) EvRange,
    (int Min, int Max) Kelvin,
    bool Tint,
    float MinFocusDiopters,
    bool ManualFocus,
    bool OisToggle,
    bool Regions,
    bool Raw)
{
    /// <summary>
    ///     A camera that offers nothing but a picture — the honest answer for desktop capture,
    ///     and for an Android device at the LEGACY hardware level. A UI built from this shows
    ///     the frame, the shutter and the looks, and no dials at all.
    /// </summary>
    public static CameraCapabilities None { get; } = new(
        Iso: default,
        Shutter: default,
        EvStep: 0f,
        EvRange: (0, 0),
        Kelvin: (0, 0),
        Tint: false,
        MinFocusDiopters: 0f,
        ManualFocus: false,
        OisToggle: false,
        Regions: false,
        Raw: false
    );

    /// <summary>True when any dial at all is worth drawing.</summary>
    public bool AnyManual => Iso.Supported || Shutter.Supported || ManualFocus || Kelvin.Max > 0;

    /// <summary>EV compensation bounds expressed in stops, which is what a dial shows.</summary>
    public (float Min, float Max) EvStops =>
        EvStep <= 0f ? (0f, 0f) : (EvRange.Min * EvStep, EvRange.Max * EvStep);
}

/// <summary>
///     What the sensor actually did for one frame, as opposed to what was asked of it. The
///     viewfinder readout shows these — in Auto they are the only way to know the exposure, and
///     in Manual they are how you find out the device clamped you.
/// </summary>
/// <param name="Iso">Sensitivity actually used.</param>
/// <param name="ShutterNs">Exposure time actually used, in nanoseconds.</param>
/// <param name="Aperture">f-number, fixed on nearly every phone.</param>
/// <param name="FocalLengthMm">Physical focal length of the active lens.</param>
/// <param name="FocusDiopters">Lens position, in diopters; NaN when the device does not report it.</param>
/// <param name="Kelvin">Estimated colour temperature, 0 when the device does not report it.</param>
/// <param name="AeConverged">False while the device is still hunting for exposure.</param>
/// <param name="AfConverged">False while the lens is still hunting for focus.</param>
public sealed record CaptureMetadata(
    int Iso,
    long ShutterNs,
    float Aperture,
    float FocalLengthMm,
    float FocusDiopters,
    int Kelvin,
    bool AeConverged,
    bool AfConverged)
{
    /// <summary>"1/250", or "1.3\"" once past a second — how a camera body writes it.</summary>
    public string ShutterLabel
    {
        get
        {
            if (ShutterNs <= 0) return "—";
            double seconds = ShutterNs / 1_000_000_000.0;
            if (seconds >= 1.0) return $"{seconds:0.#}\"";
            return $"1/{Math.Round(1.0 / seconds)}";
        }
    }
}

/// <summary>
///     An immutable read of every control at one instant. The driver is handed one of these
///     instead of thirteen separate setters: a capture request is rebuilt wholesale anyway, and a
///     snapshot cannot tear halfway through while the app thread is changing a dial.
/// </summary>
public readonly record struct ControlState(
    int Iso,
    int AutoIsoMax,
    long ShutterNs,
    long MinAutoShutterNs,
    float EvCompensation,
    int WhiteBalanceKelvin,
    float WhiteBalanceTint,
    float FocusDiopters,
    bool Ois,
    Rect? AeRegion,
    Rect? AfRegion,
    bool AeLock,
    bool AfLock,
    bool AwbLock)
{
    /// <summary>ISO is being driven by hand.</summary>
    public bool IsoManual => Iso > 0;

    /// <summary>Shutter is being driven by hand.</summary>
    public bool ShutterManual => ShutterNs > 0;

    /// <summary>Focus is being driven by hand (a NaN focus means autofocus).</summary>
    public bool FocusManual => !float.IsNaN(FocusDiopters);

    /// <summary>White balance is being driven by hand.</summary>
    public bool WhiteBalanceManual => WhiteBalanceKelvin > 0;

    /// <summary>
    ///     The mode the dials add up to. Nothing stores this — which is the point: it can never
    ///     disagree with the controls it describes.
    /// </summary>
    public ExposureMode Mode => (IsoManual, ShutterManual) switch {
        (true, true) => ExposureMode.Manual,
        (true, false) => ExposureMode.IsoPriority,
        (false, true) => ExposureMode.ShutterPriority,
        _ => ExposureMode.Auto,
    };
}

/// <summary>
///     The manual controls, one signal per parameter, so a dial binds straight to one and a
///     readout binds straight to another. Every parameter has an "the device decides" value —
///     0 for ISO, shutter and Kelvin, NaN for focus — because handing a control back to the
///     camera is a thing photographers do constantly and must not need a separate mode switch.
///     <para>
///         Setting a control the running camera cannot do is not an error: the driver applies
///         what it can and <see cref="CameraController.Metadata" /> reports what the sensor
///         actually did. Ask <see cref="CameraController.Capabilities" /> before drawing a dial.
///     </para>
/// </summary>
public sealed class CameraControls
{
    /// <summary>Sensitivity, or 0 to let the device meter it.</summary>
    public Signal<int> Iso { get; } = new(0);

    /// <summary>Ceiling for device-chosen ISO, or 0 for the device's own ceiling.</summary>
    public Signal<int> AutoIsoMax { get; } = new(0);

    /// <summary>Exposure time in nanoseconds, or 0 to let the device meter it.</summary>
    public Signal<long> ShutterNs { get; } = new(0);

    /// <summary>Slowest shutter the device may choose on its own, or 0 for its own floor.</summary>
    public Signal<long> MinAutoShutterNs { get; } = new(0);

    /// <summary>Exposure compensation in stops. Applies whenever the device is metering.</summary>
    public Signal<float> EvCompensation { get; } = new(0f);

    /// <summary>Colour temperature in kelvin, or 0 for auto white balance.</summary>
    public Signal<int> WhiteBalanceKelvin { get; } = new(0);

    /// <summary>Green/magenta tint, −1…1, 0 neutral. Only meaningful with a manual temperature.</summary>
    public Signal<float> WhiteBalanceTint { get; } = new(0f);

    /// <summary>Lens position in diopters (1/m), or NaN for autofocus.</summary>
    public Signal<float> FocusDiopters { get; } = new(float.NaN);

    /// <summary>Optical stabilization, where the device lets it be switched off.</summary>
    public Signal<bool> Ois { get; } = new(true);

    /// <summary>Where to meter exposure, in normalized frame coordinates, or null for the default.</summary>
    public Signal<Rect?> AeRegion { get; } = new(null);

    /// <summary>Where to focus, or null to follow <see cref="AeRegion" /> — the merged reticle.</summary>
    public Signal<Rect?> AfRegion { get; } = new(null);

    /// <summary>Hold the metered exposure.</summary>
    public Signal<bool> AeLock { get; } = new(false);

    /// <summary>Hold focus where it is.</summary>
    public Signal<bool> AfLock { get; } = new(false);

    /// <summary>Hold the metered white balance.</summary>
    public Signal<bool> AwbLock { get; } = new(false);

    /// <summary>The exposure mode the current dials add up to.</summary>
    public ExposureMode Mode => Snapshot().Mode;

    /// <summary>Every signal, for one coalesced subscription instead of fourteen.</summary>
    internal ISignal[] All =>
    [
        Iso, AutoIsoMax, ShutterNs, MinAutoShutterNs, EvCompensation,
        WhiteBalanceKelvin, WhiteBalanceTint, FocusDiopters, Ois,
        AeRegion, AfRegion, AeLock, AfLock, AwbLock,
    ];

    /// <summary>Read every control at once, for handing to a driver.</summary>
    public ControlState Snapshot() => new(
        Iso: Iso.Value,
        AutoIsoMax: AutoIsoMax.Value,
        ShutterNs: ShutterNs.Value,
        MinAutoShutterNs: MinAutoShutterNs.Value,
        EvCompensation: EvCompensation.Value,
        WhiteBalanceKelvin: WhiteBalanceKelvin.Value,
        WhiteBalanceTint: WhiteBalanceTint.Value,
        FocusDiopters: FocusDiopters.Value,
        Ois: Ois.Value,
        AeRegion: AeRegion.Value,
        AfRegion: AfRegion.Value,
        AeLock: AeLock.Value,
        AfLock: AfLock.Value,
        AwbLock: AwbLock.Value
    );

    /// <summary>
    ///     Hand everything back to the camera. What a "reset to auto" button does, and what a new
    ///     session starts from.
    /// </summary>
    public void ResetToAuto()
    {
        Iso.Value = 0;
        ShutterNs.Value = 0;
        WhiteBalanceKelvin.Value = 0;
        WhiteBalanceTint.Value = 0f;
        FocusDiopters.Value = float.NaN;
        EvCompensation.Value = 0f;
        AeLock.Value = false;
        AfLock.Value = false;
        AwbLock.Value = false;
        AeRegion.Value = null;
        AfRegion.Value = null;
    }

    /// <summary>
    ///     Pull the manual values within what this camera can do, leaving "auto" values alone.
    ///     Called when a session opens or the lens changes, so a dial carried over from a camera
    ///     with a wider range lands somewhere legal instead of being silently ignored.
    /// </summary>
    public void ClampTo(CameraCapabilities caps)
    {
        if (Iso.Value > 0) Iso.Value = caps.Iso.Supported ? caps.Iso.Clamp(Iso.Value) : 0;
        if (AutoIsoMax.Value > 0 && caps.Iso.Supported) AutoIsoMax.Value = caps.Iso.Clamp(AutoIsoMax.Value);

        if (ShutterNs.Value > 0)
            ShutterNs.Value = caps.Shutter.Supported ? caps.Shutter.Clamp(ShutterNs.Value) : 0;

        if (WhiteBalanceKelvin.Value > 0)
            WhiteBalanceKelvin.Value = caps.Kelvin.Max > 0
                ? Math.Clamp(value: WhiteBalanceKelvin.Value, min: caps.Kelvin.Min, max: caps.Kelvin.Max)
                : 0;

        if (!float.IsNaN(FocusDiopters.Value) && !caps.ManualFocus) FocusDiopters.Value = float.NaN;
        if (!caps.Tint) WhiteBalanceTint.Value = 0f;
        if (!caps.OisToggle) Ois.Value = true;

        (float minEv, float maxEv) = caps.EvStops;
        EvCompensation.Value = maxEv > minEv
            ? Math.Clamp(value: EvCompensation.Value, min: minEv, max: maxEv)
            : 0f;
    }
}

/// <summary>
///     Colour temperature as RGB channel gains. Camera2 has no Kelvin API at all — it takes
///     per-channel gains and a colour transform — so the conversion has to live somewhere, and
///     a shared one keeps 5200 K meaning the same thing on both platforms.
/// </summary>
public static class WhiteBalance
{
    /// <summary>The range every supported device covers; also the dial's bounds.</summary>
    public const int MinKelvin = 2000;

    public const int MaxKelvin = 10000;

    /// <summary>
    ///     Gains that turn a scene at <paramref name="kelvin" /> into a neutral frame, normalized
    ///     so green is 1.0 (which is what Camera2's <c>COLOR_CORRECTION_GAINS</c> expects).
    ///     <paramref name="tint" /> runs −1 (green) to +1 (magenta).
    ///     <para>
    ///         The white point comes from a standard blackbody approximation, then inverts: a warm
    ///         scene (low kelvin) is corrected by pulling red down and blue up. Cooling a picture
    ///         means telling the camera the light was warmer, which is why the dial feels
    ///         backwards to anyone who has not used one — and right to everyone who has.
    ///     </para>
    /// </summary>
    public static (float R, float G, float B) GainsFor(int kelvin, float tint = 0f)
    {
        int k = Math.Clamp(value: kelvin, min: MinKelvin, max: MaxKelvin);
        (float wr, float wg, float wb) = WhitePoint(k);

        // Invert the illuminant to correct for it, then normalize on green.
        float r = wg / wr;
        float g = 1f;
        float b = wg / wb;

        // Tint trades green against the red/blue pair, so overall brightness holds roughly still.
        float t = Math.Clamp(value: tint, min: -1f, max: 1f);
        g *= 1f - (t * 0.3f);
        r *= 1f + (t * 0.15f);
        b *= 1f + (t * 0.15f);

        // Camera2 rejects a gain below 1.0 on any channel: scale the set up rather than clipping
        // one channel, which would shift the hue instead of the exposure.
        float smallest = Math.Min(val1: Math.Min(val1: r, val2: g), val2: b);
        if (smallest < 1f && smallest > 0f)
        {
            float scale = 1f / smallest;
            r *= scale;
            g *= scale;
            b *= scale;
        }

        return (r, g, b);
    }

    /// <summary>
    ///     Approximate sRGB of a blackbody at <paramref name="kelvin" />, normalized to green.
    ///     Tanner Helland's fit — good to a couple of percent over 2000–10000 K, which is far
    ///     inside what a white-balance dial can be read to.
    /// </summary>
    internal static (float R, float G, float B) WhitePoint(int kelvin)
    {
        double t = Math.Clamp(value: kelvin, min: MinKelvin, max: MaxKelvin) / 100.0;

        double r = t <= 66
            ? 255
            : 329.698727446 * Math.Pow(x: t - 60, y: -0.1332047592);

        double g = t <= 66
            ? (99.4708025861 * Math.Log(t)) - 161.1195681661
            : 288.1221695283 * Math.Pow(x: t - 60, y: -0.0755148492);

        double b = t >= 66
            ? 255
            : t <= 19
                ? 0
                : (138.5177312231 * Math.Log(t - 10)) - 305.0447927307;

        double gn = Math.Clamp(value: g, min: 1, max: 255);
        return (
            (float)(Math.Clamp(value: r, min: 1, max: 255) / gn),
            1f,
            (float)(Math.Clamp(value: b, min: 1, max: 255) / gn)
        );
    }
}
