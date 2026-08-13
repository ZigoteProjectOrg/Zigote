using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Scripting;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Backs the generic <see cref="Audio" /> scripting API with the native miniaudio engine in play
///     mode.
///     A thin pass-through over <see cref="ZigoteEngine" /> — source ids round-trip 1:1 as
///     <see cref="SoundHandle" />s. Mirrors <see cref="RuntimePhysicsBackend" /> /
///     <c>RuntimeInstancingBackend</c>.
/// </summary>
internal sealed class RuntimeAudioBackend(ZigoteEngine engine) : IAudioBackend
{
    public void SetListener(Vec3 position, Vec3 forward, Vec3 up) =>
        engine.AudioSetListener(position: position, forward: forward, up: up);

    public void SetMasterVolume(float volume) => engine.AudioSetMasterVolume(volume);

    public void PlayUiTone(float frequencyHz, float durationSeconds, float volume, SoundWave wave)
    {
        engine.AudioBeep(
            frequencyHz: frequencyHz,
            durationSeconds: durationSeconds,
            volume: volume,
            waveform: (int)wave
        );
    }

    public void PlayToneAt(Vec3 position, float frequencyHz, float durationSeconds, float volume,
        SoundWave wave,
        float minDistance, float maxDistance, float rolloff)
    {
        engine.AudioBeep3D(
            position: position,
            frequencyHz: frequencyHz,
            durationSeconds: durationSeconds,
            volume: volume,
            waveform: (int)wave,
            minDistance: minDistance,
            maxDistance: maxDistance,
            rolloff: rolloff
        );
    }

    public SoundHandle CreateTone(float frequencyHz, SoundWave wave) => new(
        engine.AudioSoundCreateTone(frequencyHz: frequencyHz, waveform: (int)wave)
    );

    public SoundHandle CreateFile(string path, bool streaming)
    {
        string resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        return new SoundHandle(engine.AudioSoundCreateFile(path: resolved, streaming: streaming));
    }

    public void Play(SoundHandle sound) => engine.AudioSoundPlay(sound.Id);

    public void Stop(SoundHandle sound) => engine.AudioSoundStop(sound.Id);

    public void Destroy(SoundHandle sound) => engine.AudioSoundDestroy(sound.Id);

    public void SetVolume(SoundHandle sound, float volume) =>
        engine.AudioSoundSetVolume(id: sound.Id, volume: volume);

    public void SetPitch(SoundHandle sound, float pitch) =>
        engine.AudioSoundSetPitch(id: sound.Id, pitch: pitch);

    public void SetLooping(SoundHandle sound, bool looping) =>
        engine.AudioSoundSetLooping(id: sound.Id, looping: looping);

    public void SetSpatial(SoundHandle sound, bool enabled) =>
        engine.AudioSoundSetSpatial(id: sound.Id, enabled: enabled);

    public void SetPosition(SoundHandle sound, Vec3 position) =>
        engine.AudioSoundSetPosition(id: sound.Id, position: position);

    public void SetVelocity(SoundHandle sound, Vec3 velocity) =>
        engine.AudioSoundSetVelocity(id: sound.Id, velocity: velocity);

    public void SetAttenuation(SoundHandle sound, float minDistance, float maxDistance,
        float rolloff)
    {
        engine.AudioSoundSetAttenuation(
            id: sound.Id,
            minDistance: minDistance,
            maxDistance: maxDistance,
            rolloff: rolloff
        );
    }

    public bool IsPlaying(SoundHandle sound) => engine.AudioSoundIsPlaying(sound.Id);

    public AudioBus CreateBus() => new(engine.AudioGroupCreate());

    public void SetBusVolume(AudioBus bus, float volume) =>
        engine.AudioGroupSetVolume(groupId: bus.Id, volume: volume);

    public void SetBusPitch(AudioBus bus, float pitch) =>
        engine.AudioGroupSetPitch(groupId: bus.Id, pitch: pitch);

    public void SetBus(SoundHandle sound, AudioBus bus) =>
        engine.AudioSoundSetGroup(id: sound.Id, groupId: bus.Id);

    public void StopAll() => engine.AudioStopAll();

    // ── Streaming + transport ────────────────────────────────────────────────

    public SoundHandle CreateStream() => new(engine.AudioStreamCreate());

    public int PushStream(SoundHandle sound, ReadOnlySpan<byte> bytes) =>
        engine.AudioStreamPush(id: sound.Id, bytes: bytes);

    public void FinishStream(SoundHandle sound) => engine.AudioStreamFinish(sound.Id);

    public float StreamBuffered(SoundHandle sound) => engine.AudioStreamBuffered(sound.Id);

    public float GetCursor(SoundHandle sound) => engine.AudioSoundCursor(sound.Id);

    public float GetDuration(SoundHandle sound) => engine.AudioSoundDuration(sound.Id);

    public void Seek(SoundHandle sound, float seconds) => engine.AudioSoundSeek(
        id: sound.Id,
        seconds: MathF.Max(x: 0f, y: seconds)
    );
}
