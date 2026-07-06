// Sample script — Samples/Scripting/SoundEmitter.cs
// Copy to your project and reference Zigote.Scripting.dll to get started.

using Zigote.Scripting;

namespace Samples.Scripting;

/// <summary>
///     Emits a looping, spatialised procedural tone from the attached node and keeps it positioned at
///     the
///     node each frame — drive the node around (or with a <c>Rotator</c>) and the sound orbits the
///     listener.
///     Demonstrates the generic <see cref="Audio" /> scripting API; the editor's play session backs
///     it.
/// </summary>
public sealed class SoundEmitter : Component
{
    private SoundHandle _sound;

    [Export]
    [EditorRange(20, 4000)]
    [EditorTooltip("Tone frequency in Hz")]
    public float Frequency { get; set; } = 220f;

    [Export]
    [EditorRange(0, 4)]
    [EditorTooltip("Waveform: 0 sine, 1 square, 2 triangle, 3 saw, 4 noise")]
    public int Wave { get; set; }

    [Export] [EditorRange(0, 1)] public float Volume { get; set; } = 0.6f;

    [Export]
    [EditorRange(1, 200)]
    [EditorTooltip("Distance (m) beyond which the sound is silent")]
    public float MaxDistance { get; set; } = 40f;

    protected override void OnCreate()
    {
        _sound = Audio.CreateTone(Frequency, (SoundWave)Math.Clamp(Wave, 0, 4));
        if (!_sound.IsValid) return;

        Audio.SetSpatial(_sound, true);
        Audio.SetLooping(_sound, true);
        Audio.SetVolume(_sound, Volume);
        Audio.SetAttenuation(
            _sound,
            1f,
            MaxDistance,
            1f
        );
        Audio.SetPosition(_sound, Position);
        Audio.Play(_sound);
    }

    protected override void OnUpdate(float deltaTime)
    {
        if (_sound.IsValid) Audio.SetPosition(_sound, Position);
    }

    protected override void OnDestroy()
    {
        if (!_sound.IsValid) return;
        Audio.Stop(_sound);
        Audio.Destroy(_sound);
        _sound = SoundHandle.None;
    }
}