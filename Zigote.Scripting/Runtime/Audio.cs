using Zigote.Core.Math3D;

namespace Zigote.Scripting;

/// <summary>Procedural oscillator shape for tones (matches the engine waveform codes).</summary>
public enum SoundWave : byte
{
    Sine = 0,
    Square = 1,
    Triangle = 2,
    Sawtooth = 3,
    Noise = 4,
}

/// <summary>A lightweight, copyable handle to an addressable audio source owned by the engine.</summary>
public readonly struct SoundHandle(uint id) : IEquatable<SoundHandle>
{
    public static SoundHandle None => new(0);

    public uint Id { get; } = id;
    public bool IsValid => Id != 0;

    public bool Equals(SoundHandle other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is SoundHandle h && Equals(h);

    public override int GetHashCode() => (int)Id;

    public static bool operator ==(SoundHandle a, SoundHandle b) => a.Id == b.Id;

    public static bool operator !=(SoundHandle a, SoundHandle b) => a.Id != b.Id;
}

/// <summary>
///     A lightweight, copyable handle to a mixer bus (a miniaudio sound group). Route sounds through
///     buses to control whole categories at once — music/sfx sliders, pause ducking. Buses live for
///     the whole play session; create a handful once, there is no per-bus destroy.
/// </summary>
public readonly struct AudioBus(uint id) : IEquatable<AudioBus>
{
    public static AudioBus None => new(0);

    public uint Id { get; } = id;
    public bool IsValid => Id != 0;

    public bool Equals(AudioBus other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is AudioBus b && Equals(b);

    public override int GetHashCode() => (int)Id;

    public static bool operator ==(AudioBus a, AudioBus b) => a.Id == b.Id;

    public static bool operator !=(AudioBus a, AudioBus b) => a.Id != b.Id;
}

/// <summary>
///     The contract the host (editor play session / game runtime) implements to back the generic
///     <see cref="Audio" /> scripting API with a real spatial-audio engine. A strongly-typed interface
///     (rather than multiplexed delegates) so it stays debuggable and headless tests can inject a
///     fake.
///     Mirrors <see cref="IPhysicsBackend" />.
/// </summary>
public interface IAudioBackend
{
    /// <summary>Set the spatial listener pose; every spatialised sound pans/attenuates against it.</summary>
    void SetListener(Vec3 position, Vec3 forward, Vec3 up);

    /// <summary>Master output volume [0,4]; 1 = unity.</summary>
    void SetMasterVolume(float volume);

    /// <summary>Fire a non-spatial procedural one-shot (UI click / blip).</summary>
    void PlayUiTone(float frequencyHz, float durationSeconds, float volume, SoundWave wave);

    /// <summary>Fire a positioned procedural one-shot (spatialised + distance-attenuated).</summary>
    void PlayToneAt(Vec3 position, float frequencyHz, float durationSeconds, float volume,
        SoundWave wave,
        float minDistance, float maxDistance, float rolloff);

    /// <summary>
    ///     Create a sustained procedural-tone source (not started). <see cref="SoundHandle.None" />
    ///     on failure.
    /// </summary>
    SoundHandle CreateTone(float frequencyHz, SoundWave wave);

    /// <summary>
    ///     Create a source from an audio file (WAV/OGG/MP3/FLAC; not started).
    ///     <see cref="SoundHandle.None" /> on
    ///     failure.
    /// </summary>
    SoundHandle CreateFile(string path, bool streaming);

    void Play(SoundHandle sound);
    void Stop(SoundHandle sound);
    void Destroy(SoundHandle sound);

    void SetVolume(SoundHandle sound, float volume);
    void SetPitch(SoundHandle sound, float pitch);
    void SetLooping(SoundHandle sound, bool looping);
    void SetSpatial(SoundHandle sound, bool enabled);
    void SetPosition(SoundHandle sound, Vec3 position);
    void SetVelocity(SoundHandle sound, Vec3 velocity);
    void SetAttenuation(SoundHandle sound, float minDistance, float maxDistance, float rolloff);

    bool IsPlaying(SoundHandle sound);

    // Streaming and transport are optional: they default to "not supported" so a backend that only
    // needs tones and files — a test fake, a headless host — stays a handful of methods.

    /// <summary>
    ///     Create a source fed by <see cref="PushStream" /> rather than from a file — an internet
    ///     radio station, or anything else arriving as bytes over time.
    /// </summary>
    SoundHandle CreateStream() => SoundHandle.None;

    /// <summary>Hand container bytes to a stream source. Returns how many were accepted.</summary>
    int PushStream(SoundHandle sound, ReadOnlySpan<byte> bytes) => 0;

    /// <summary>No more bytes are coming; the source plays out what it has and ends.</summary>
    void FinishStream(SoundHandle sound) { }

    /// <summary>Seconds of audio buffered ahead of the play cursor — the underrun early warning.</summary>
    float StreamBuffered(SoundHandle sound) => 0f;

    /// <summary>Play position in seconds, or −1 when unknown.</summary>
    float GetCursor(SoundHandle sound) => -1f;

    /// <summary>Total length in seconds, or −1 for a stream of unknown length.</summary>
    float GetDuration(SoundHandle sound) => -1f;

    /// <summary>Jump the play cursor. The lever shared playback uses to correct drift.</summary>
    void Seek(SoundHandle sound, float seconds) { }

    /// <summary>
    ///     Create a mixer bus. <see cref="AudioBus.None" /> on failure. Buses live for the whole
    ///     play session — create a handful once (music/sfx/ambience), there is no per-bus destroy.
    /// </summary>
    AudioBus CreateBus();

    /// <summary>Bus output volume [0,4]; 1 = unity. The pause-duck lever: scale a whole bus at once.</summary>
    void SetBusVolume(AudioBus bus, float volume);

    /// <summary>Bus playback-rate multiplier (affects every sound routed through it).</summary>
    void SetBusPitch(AudioBus bus, float pitch);

    /// <summary>Route a sound through a bus (<see cref="AudioBus.None" /> = back to the master output).</summary>
    void SetBus(SoundHandle sound, AudioBus bus);

    /// <summary>Silence + free every sound (one-shots, channels, and handle sources).</summary>
    void StopAll();
}

/// <summary>
///     Generic spatial / surround audio for scripts. Engine-generic — it knows nothing about the
///     editor.
///     The host assigns <see cref="Backend" /> in play mode (and clears it on stop); outside play
///     every
///     call is a safe no-op. Mirrors <see cref="Input" /> / <see cref="Physics" />.
///     <para>
///         Two usage patterns: fire-and-forget procedural one-shots (<see cref="PlayToneAt" /> /
///         <see cref="PlayUiTone" />) need no cleanup; addressable sources (<see cref="CreateTone" />
///         /
///         <see cref="CreateFile" />) are load-once / control-explicitly — the script owns their
///         lifetime
///         and should <see cref="Destroy" /> them in <c>OnDestroy</c>.
///     </para>
/// </summary>
public static class Audio
{
    /// <summary>Set by the host (or a test) to route calls to a real audio engine.</summary>
    public static IAudioBackend? Backend { get; set; }

    public static bool IsAvailable => Backend != null;

    public static void SetListener(Vec3 position, Vec3 forward, Vec3 up) =>
        Backend?.SetListener(position: position, forward: forward, up: up);

    public static void SetMasterVolume(float volume) => Backend?.SetMasterVolume(volume);

    public static void PlayUiTone(float frequencyHz, float durationSeconds = 0.08f,
        float volume = 0.5f,
        SoundWave wave = SoundWave.Sine)
    {
        Backend?.PlayUiTone(
            frequencyHz: frequencyHz,
            durationSeconds: durationSeconds,
            volume: volume,
            wave: wave
        );
    }

    public static void PlayToneAt(Vec3 position, float frequencyHz, float durationSeconds = 0.2f,
        float volume = 1f, SoundWave wave = SoundWave.Sine, float minDistance = 1f,
        float maxDistance = 50f,
        float rolloff = 1f)
    {
        Backend?.PlayToneAt(
            position: position,
            frequencyHz: frequencyHz,
            durationSeconds: durationSeconds,
            volume: volume,
            wave: wave,
            minDistance: minDistance,
            maxDistance: maxDistance,
            rolloff: rolloff
        );
    }

    public static SoundHandle CreateTone(float frequencyHz, SoundWave wave = SoundWave.Sine) =>
        Backend?.CreateTone(frequencyHz: frequencyHz, wave: wave) ?? SoundHandle.None;

    public static SoundHandle CreateFile(string path, bool streaming = false) =>
        Backend?.CreateFile(path: path, streaming: streaming) ?? SoundHandle.None;

    public static void Play(SoundHandle sound) => Backend?.Play(sound);

    public static void Stop(SoundHandle sound) => Backend?.Stop(sound);

    public static void Destroy(SoundHandle sound) => Backend?.Destroy(sound);

    public static void SetVolume(SoundHandle sound, float volume) =>
        Backend?.SetVolume(sound: sound, volume: volume);

    public static void SetPitch(SoundHandle sound, float pitch) =>
        Backend?.SetPitch(sound: sound, pitch: pitch);

    /// <summary>
    ///     A source fed by <see cref="PushStream" /> instead of a file. Push container bytes as they
    ///     arrive — the decoder identifies the format once enough have landed — and the source plays
    ///     continuously. Spatialise it like any other sound.
    /// </summary>
    public static SoundHandle CreateStream() => Backend?.CreateStream() ?? SoundHandle.None;

    public static int PushStream(SoundHandle sound, ReadOnlySpan<byte> bytes) =>
        Backend?.PushStream(sound: sound, bytes: bytes) ?? 0;

    public static void FinishStream(SoundHandle sound) => Backend?.FinishStream(sound);

    public static float StreamBuffered(SoundHandle sound) => Backend?.StreamBuffered(sound) ?? 0f;

    /// <summary>Play position in seconds, or −1 when unknown.</summary>
    public static float GetCursor(SoundHandle sound) => Backend?.GetCursor(sound) ?? -1f;

    public static float GetDuration(SoundHandle sound) => Backend?.GetDuration(sound) ?? -1f;

    /// <summary>Jump the play cursor — how shared playback pulls a drifting listener back into line.</summary>
    public static void Seek(SoundHandle sound, float seconds) =>
        Backend?.Seek(sound: sound, seconds: seconds);

    public static void SetLooping(SoundHandle sound, bool looping) =>
        Backend?.SetLooping(sound: sound, looping: looping);

    public static void SetSpatial(SoundHandle sound, bool enabled) =>
        Backend?.SetSpatial(sound: sound, enabled: enabled);

    public static void SetPosition(SoundHandle sound, Vec3 position) =>
        Backend?.SetPosition(sound: sound, position: position);

    public static void SetVelocity(SoundHandle sound, Vec3 velocity) =>
        Backend?.SetVelocity(sound: sound, velocity: velocity);

    public static void SetAttenuation(SoundHandle sound, float minDistance, float maxDistance,
        float rolloff)
    {
        Backend?.SetAttenuation(
            sound: sound,
            minDistance: minDistance,
            maxDistance: maxDistance,
            rolloff: rolloff
        );
    }

    public static bool IsPlaying(SoundHandle sound) => Backend?.IsPlaying(sound) ?? false;

    public static AudioBus CreateBus() => Backend?.CreateBus() ?? AudioBus.None;

    public static void SetBusVolume(AudioBus bus, float volume) =>
        Backend?.SetBusVolume(bus: bus, volume: volume);

    public static void SetBusPitch(AudioBus bus, float pitch) =>
        Backend?.SetBusPitch(bus: bus, pitch: pitch);

    public static void SetBus(SoundHandle sound, AudioBus bus) =>
        Backend?.SetBus(sound: sound, bus: bus);

    public static void StopAll() => Backend?.StopAll();
}
