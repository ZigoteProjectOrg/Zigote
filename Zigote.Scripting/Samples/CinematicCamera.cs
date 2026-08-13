// Sample script — Samples/Scripting/CinematicCamera.cs
// Copy to your project and reference Zigote.Scripting.dll to get started.

using Zigote.Cinematics;
using Zigote.Scripting;

namespace Samples.Scripting;

/// <summary>
///     Attach to the Camera node to give it a physical lens + film look at runtime, without editor
///     authoring. Publishes its settings through the generic <see cref="Camera" /> API each frame; the
///     host resolves them into the render (FOV / depth of field / exposure / film grade). Mutate the
///     exported fields from other scripts for dolly zooms, rack focus, or film-stock changes.
/// </summary>
public sealed class CinematicCamera : Component
{
    [Export]
    [EditorTooltip("Master switch for the physical camera")]
    public bool Enabled { get; set; } = true;

    [Export]
    [EditorRange(min: 8, max: 800)]
    [EditorTooltip("Lens focal length in millimetres")]
    public float FocalLengthMm { get; set; } = 50f;

    [Export]
    [EditorRange(min: 1, max: 22)]
    [EditorTooltip("Aperture f-stop (lower = shallower depth of field)")]
    public float FStop { get; set; } = 2.8f;

    [Export]
    [EditorRange(min: 50, max: 25600)]
    public float Iso { get; set; } = 100f;

    [Export]
    [EditorTooltip("Shutter time in seconds (e.g. 0.02 = 1/50)")]
    public float ShutterSpeed { get; set; } = 1f / 50f;

    [Export]
    [EditorRange(min: 0, max: 6)]
    [EditorTooltip(
        "Film stock: 0 Neutral, 1 Kodak2383, 2 Vision3, 3 Eterna, 4 Ektachrome, 5 Cineon, 6 B&W"
    )]
    public int FilmStock { get; set; }

    [Export] [EditorRange(min: 0, max: 1)] public float FilmStrength { get; set; } = 1f;

    [Export]
    [EditorRange(min: 0, max: 2)]
    [EditorTooltip("Focus: 0 Manual, 1 Center AF, 2 Subject AF")]
    public int FocusMode { get; set; } = 1;

    [Export]
    [EditorTooltip("Focus distance (m) for Manual mode; drive this for rack focus")]
    public float ManualFocusM { get; set; } = 8f;

    protected override void OnUpdate(float dt)
    {
        Camera.SetPhysicalEnabled(Enabled);
        if (!Enabled) return;

        Camera.SetFocalLength(FocalLengthMm);
        Camera.SetAperture(FStop);
        Camera.SetIso(Iso);
        Camera.SetShutter(ShutterSpeed);
        Camera.SetFilmStock(
            stock: (FilmStockKind)Math.Clamp(value: FilmStock, min: 0, max: 6),
            strength: FilmStrength
        );
        Camera.SetFocusMode((FocusModeKind)Math.Clamp(value: FocusMode, min: 0, max: 2));
        Camera.SetManualFocus(ManualFocusM);
    }
}
