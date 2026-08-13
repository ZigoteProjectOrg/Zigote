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
    public void SetListener(Vec3 position, Vec3 forward, Vec3 up)
    {
        engine.AudioSetListener(position, forward, up);
    }

    public void SetMasterVolume(float volume)
    {
        engine.AudioSetMasterVolume(volume);
    }

    public void PlayUiTone(float frequencyHz, float durationSeconds, float volume, SoundWave wave)
    {
        engine.AudioBeep(
            frequencyHz,
            durationSeconds,
            volume,
            (int)wave
        );
    }

    public void PlayToneAt(Vec3 position, float frequencyHz, float durationSeconds, float volume,
        SoundWave wave,
        float minDistance, float maxDistance, float rolloff)
    {
        engine.AudioBeep3D(
            position,
            frequencyHz,
            durationSeconds,
            volume,
            (int)wave,
            minDistance,
            maxDistance,
            rolloff
        );
    }

    public SoundHandle CreateTone(float frequencyHz, SoundWave wave)
    {
        return new SoundHandle(engine.AudioSoundCreateTone(frequencyHz, (int)wave));
    }

    public SoundHandle CreateFile(string path, bool streaming)
    {
        var resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        return new SoundHandle(engine.AudioSoundCreateFile(resolved, streaming));
    }

    public void Play(SoundHandle sound)
    {
        engine.AudioSoundPlay(sound.Id);
    }

    public void Stop(SoundHandle sound)
    {
        engine.AudioSoundStop(sound.Id);
    }

    public void Destroy(SoundHandle sound)
    {
        engine.AudioSoundDestroy(sound.Id);
    }

    public void SetVolume(SoundHandle sound, float volume)
    {
        engine.AudioSoundSetVolume(sound.Id, volume);
    }

    public void SetPitch(SoundHandle sound, float pitch)
    {
        engine.AudioSoundSetPitch(sound.Id, pitch);
    }

    public void SetLooping(SoundHandle sound, bool looping)
    {
        engine.AudioSoundSetLooping(sound.Id, looping);
    }

    public void SetSpatial(SoundHandle sound, bool enabled)
    {
        engine.AudioSoundSetSpatial(sound.Id, enabled);
    }

    public void SetPosition(SoundHandle sound, Vec3 position)
    {
        engine.AudioSoundSetPosition(sound.Id, position);
    }

    public void SetVelocity(SoundHandle sound, Vec3 velocity)
    {
        engine.AudioSoundSetVelocity(sound.Id, velocity);
    }

    public void SetAttenuation(SoundHandle sound, float minDistance, float maxDistance,
        float rolloff)
    {
        engine.AudioSoundSetAttenuation(
            sound.Id,
            minDistance,
            maxDistance,
            rolloff
        );
    }

    public bool IsPlaying(SoundHandle sound)
    {
        return engine.AudioSoundIsPlaying(sound.Id);
    }

    public AudioBus CreateBus()
    {
        return new AudioBus(engine.AudioGroupCreate());
    }

    public void SetBusVolume(AudioBus bus, float volume)
    {
        engine.AudioGroupSetVolume(bus.Id, volume);
    }

    public void SetBusPitch(AudioBus bus, float pitch)
    {
        engine.AudioGroupSetPitch(bus.Id, pitch);
    }

    public void SetBus(SoundHandle sound, AudioBus bus)
    {
        engine.AudioSoundSetGroup(sound.Id, bus.Id);
    }

    public void StopAll()
    {
        engine.AudioStopAll();
    }

    // ── Streaming + transport ────────────────────────────────────────────────

    public SoundHandle CreateStream()
    {
        return new SoundHandle(engine.AudioStreamCreate());
    }

    public int PushStream(SoundHandle sound, ReadOnlySpan<byte> bytes)
    {
        return engine.AudioStreamPush(sound.Id, bytes);
    }

    public void FinishStream(SoundHandle sound)
    {
        engine.AudioStreamFinish(sound.Id);
    }

    public float StreamBuffered(SoundHandle sound)
    {
        return engine.AudioStreamBuffered(sound.Id);
    }

    public float GetCursor(SoundHandle sound)
    {
        return engine.AudioSoundCursor(sound.Id);
    }

    public float GetDuration(SoundHandle sound)
    {
        return engine.AudioSoundDuration(sound.Id);
    }

    public void Seek(SoundHandle sound, float seconds)
    {
        engine.AudioSoundSeek(sound.Id, MathF.Max(0f, seconds));
    }
}
