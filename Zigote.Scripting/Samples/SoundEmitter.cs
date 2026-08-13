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
    [EditorRange(min: 20, max: 4000)]
    [EditorTooltip("Tone frequency in Hz")]
    public float Frequency { get; set; } = 220f;

    [Export]
    [EditorRange(min: 0, max: 4)]
    [EditorTooltip("Waveform: 0 sine, 1 square, 2 triangle, 3 saw, 4 noise")]
    public int Wave { get; set; }

    [Export] [EditorRange(min: 0, max: 1)] public float Volume { get; set; } = 0.6f;

    [Export]
    [EditorRange(min: 1, max: 200)]
    [EditorTooltip("Distance (m) beyond which the sound is silent")]
    public float MaxDistance { get; set; } = 40f;

    protected override void OnCreate()
    {
        _sound = Audio.CreateTone(
            frequencyHz: Frequency,
            wave: (SoundWave)Math.Clamp(value: Wave, min: 0, max: 4)
        );
        if (!_sound.IsValid) return;

        Audio.SetSpatial(sound: _sound, enabled: true);
        Audio.SetLooping(sound: _sound, looping: true);
        Audio.SetVolume(sound: _sound, volume: Volume);
        Audio.SetAttenuation(
            sound: _sound,
            minDistance: 1f,
            maxDistance: MaxDistance,
            rolloff: 1f
        );
        Audio.SetPosition(sound: _sound, position: Position);
        Audio.Play(_sound);
    }

    protected override void OnUpdate(float deltaTime)
    {
        if (_sound.IsValid) Audio.SetPosition(sound: _sound, position: Position);
    }

    protected override void OnDestroy()
    {
        if (!_sound.IsValid) return;
        Audio.Stop(_sound);
        Audio.Destroy(_sound);
        _sound = SoundHandle.None;
    }
}
