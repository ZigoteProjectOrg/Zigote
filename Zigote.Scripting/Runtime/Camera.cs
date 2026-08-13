using Zigote.Cinematics;

namespace Zigote.Scripting;

/// <summary>
///     The contract the host (editor play session / game runtime) implements to back the generic
///     <see cref="Camera" /> scripting API. Drives the <em>active</em> camera's physical-camera model
///     (lens / sensor / exposure / focus / film) so scripts can zoom, rack focus, change stocks, etc. at
///     runtime. A strongly-typed interface (like <see cref="IAudioBackend" />) so a fake can back tests.
/// </summary>
public interface ICameraBackend
{
    /// <summary>Enable/disable the physical camera on the active camera node.</summary>
    void SetPhysicalEnabled(bool enabled);

    void SetFocalLength(float millimetres);
    void SetSensor(SensorPreset preset);
    void SetSensorSize(float widthMm, float heightMm);
    void SetAperture(float fStop);
    void SetIso(float iso);
    void SetShutter(float seconds);
    void SetFocusMode(FocusModeKind mode);
    void SetManualFocus(float metres);
    void SetFilmStock(FilmStockKind stock, float strength);
}

/// <summary>
///     Generic runtime control of the active camera's photographic model, for cinematic scripts (dolly
///     zooms, rack focus, lens/film switches). Engine-generic — it knows nothing about the editor. The
///     host assigns <see cref="Backend" /> in play mode and clears it on stop; outside play every call is
///     a safe no-op. Sibling of <see cref="Audio" /> / <see cref="Physics" /> / <see cref="RenderView" />
///     (which is the read-only counterpart). Enabling any lens control turns the physical camera on for
///     the active camera; combine with the editor's Physical Camera inspector block (both write the same
///     <c>SceneNode</c> fields). Photographic types (sensor / focus / film enums) live in Zigote.Cinematics.
/// </summary>
public static class Camera
{
    /// <summary>Set by the host (or a test) to route calls to the active camera.</summary>
    public static ICameraBackend? Backend { get; set; }

    public static bool IsAvailable => Backend != null;

    public static void SetPhysicalEnabled(bool enabled)
    {
        Backend?.SetPhysicalEnabled(enabled);
    }

    public static void SetFocalLength(float millimetres)
    {
        Backend?.SetFocalLength(millimetres);
    }

    public static void SetSensor(SensorPreset preset)
    {
        Backend?.SetSensor(preset);
    }

    public static void SetSensorSize(float widthMm, float heightMm)
    {
        Backend?.SetSensorSize(widthMm, heightMm);
    }

    public static void SetAperture(float fStop)
    {
        Backend?.SetAperture(fStop);
    }

    public static void SetIso(float iso)
    {
        Backend?.SetIso(iso);
    }

    public static void SetShutter(float seconds)
    {
        Backend?.SetShutter(seconds);
    }

    public static void SetFocusMode(FocusModeKind mode)
    {
        Backend?.SetFocusMode(mode);
    }

    public static void SetManualFocus(float metres)
    {
        Backend?.SetManualFocus(metres);
    }

    public static void SetFilmStock(FilmStockKind stock, float strength)
    {
        Backend?.SetFilmStock(stock, strength);
    }
}
