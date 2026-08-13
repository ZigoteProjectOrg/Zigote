namespace Zigote.Core.Engine;

/// <summary>
///     The media half of the engine's audio surface: open a file, move through it, filter it, decode
///     it. Everything a player, an editor timeline or a sampler needs, and nothing a game's spatial
///     mixer needs — listener pose, positioned one-shots and procedural voices stay on
///     <see cref="ZigoteEngine" />, because an app that never places a sound in a world should not
///     have to stub them.
///     <para>
///         It is an interface for one reason worth the indirection:
///         <b>
///             the device is the one part of
///             a player that cannot exist in CI
///         </b>
///         . A queue, a transport and an equalizer are pure state
///         machines, and behind this seam they can be driven by a fake and asserted on without a
///         sound card. <see cref="ZigoteEngine.Audio" /> is the real implementation.
///     </para>
///     <para>
///         Ids are engine-side handles; 0 always means "nothing" and is safe to pass anywhere. A
///         call against a stale id is ignored rather than fatal, which is what makes
///         <see cref="Reopen" /> — it invalidates every id at once — survivable.
///     </para>
/// </summary>
public interface IAudioApi
{
    /// <summary>The output device's current sample rate in Hz; 0 when there is no audio device.</summary>
    int OutputRate { get; }

    /// <summary>
    ///     Reopen the device at <paramref name="sampleRateHz" /> (0 = its preferred rate), returning
    ///     the rate actually achieved (0 = failure, sound now disabled).
    ///     <b>
    ///         Every sound, bus and
    ///         equalizer id becomes invalid.
    ///     </b>
    ///     The rate is fixed at device creation, so playing a
    ///     high-resolution source at its own rate is the only way to avoid resampling it.
    /// </summary>
    int Reopen(int sampleRateHz);

    /// <summary>
    ///     Open a file as a source, not started. <paramref name="streaming" /> decodes as it plays
    ///     rather than loading the whole file — what a music player wants for a 60 MB FLAC. Returns 0
    ///     on failure. Parses the container header, so it belongs on a worker thread.
    /// </summary>
    uint CreateFile(string path, bool streaming);

    /// <summary>
    ///     Open a source that is <i>pushed</i> instead of pulled: the caller hands it encoded bytes
    ///     with <see cref="StreamPush" />, and the engine decodes and mixes them like any other
    ///     sound. Returns 0 on failure.
    ///     <para>
    ///         This is what a file cannot be — a socket has no path to open and no end to seek to.
    ///         Everything downstream is unchanged, so a radio station routes through the same
    ///         equalizer chain and obeys the same transport as a track on disk.
    ///     </para>
    /// </summary>
    uint CreateStream();

    /// <summary>
    ///     Hand encoded bytes to a stream source, returning how many were taken. A short count means
    ///     its queue is full: stop reading until it drains rather than buffering without bound.
    /// </summary>
    int StreamPush(uint id, ReadOnlySpan<byte> bytes);

    /// <summary>
    ///     No more bytes are coming. What is queued still plays out, then the sound reports
    ///     end-of-stream — so a finished stream advances a playlist exactly like a finished file.
    /// </summary>
    void StreamFinish(uint id);

    /// <summary>Why a stream is or is not making sound. See <see cref="AudioStreamState" />.</summary>
    AudioStreamState StreamStatus(uint id);

    /// <summary>Decoded audio held ahead of the mixer, in seconds — the "Buffering…" number.</summary>
    float StreamBuffered(uint id);

    void Destroy(uint id);

    void Play(uint id);

    /// <summary>Stop and rewind to zero. Pausing is a stop plus a seek back to where you were.</summary>
    void Stop(uint id);

    void Seek(uint id, float seconds);

    /// <summary>
    ///     Start a sound at an exact point on the audio clock, <paramref name="secondsFromNow" />
    ///     ahead — the primitive gapless playback is built on. Polling can never be tighter than a
    ///     frame; the audio thread can hit the sample.
    /// </summary>
    void ScheduleStart(uint id, float secondsFromNow);

    /// <summary>Playback cursor in seconds; -1 when the source cannot report one.</summary>
    float Cursor(uint id);

    /// <summary>Total length in seconds; -1 when unknown (procedural tones, unseekable streams).</summary>
    float Duration(uint id);

    bool IsPlaying(uint id);

    /// <summary>
    ///     The source decoded past its last frame — a playlist's cue to advance. Unlike
    ///     <c>!IsPlaying</c> this stays false for a sound that was merely paused.
    /// </summary>
    bool AtEnd(uint id);

    /// <summary>Per-sound gain [0,4]; 1 = unity. Flat gains (preamp, ReplayGain) fold in here.</summary>
    void SetVolume(uint id, float volume);

    /// <summary>
    ///     Playback rate; 1 = as recorded. <b>Varispeed — pitch follows rate</b>, because this resamples
    ///     rather than time-stretches: 1.5× speech is 1.5× speech an octave-ish up, not a podcast app's
    ///     1.5×. Pitch-preserving playback needs a time-stretcher in the engine, not a caller-side trick.
    /// </summary>
    void SetRate(uint id, float rate);

    /// <summary>Pan and attenuate against the listener. Music is not a point in a world: pass false.</summary>
    void SetSpatial(uint id, bool enabled);

    /// <summary>Route a sound through an equalizer chain (0 = dry).</summary>
    void SetEq(uint id, uint eqId);

    /// <summary>
    ///     Create a chain of <paramref name="bandCount" /> biquad filters (max 16), flat until
    ///     configured, spliced between the sounds routed through it and the master output.
    /// </summary>
    uint EqCreate(int bandCount);

    /// <summary>
    ///     Configure one band. Shelves take Q (converted to the RBJ slope inside the engine), matching
    ///     how AutoEq and every parametric EQ UI specify them. Re-tuning without changing
    ///     <paramref name="kind" /> reconfigures the filter in place — no clicks, no graph churn.
    /// </summary>
    void EqSetBand(uint eqId, int index, AudioBandKind kind, float freqHz, float gainDb, float q);

    /// <summary>Bypass or engage the chain without losing its band settings — the A/B lever.</summary>
    void EqSetEnabled(uint eqId, bool enabled);

    void EqDestroy(uint eqId);

    /// <summary>
    ///     Decode a whole file to interleaved floats at its native rate and channel count, for callers
    ///     that need samples rather than playback (waveform overviews, loudness analysis, IR loading).
    ///     Needs no device. Blocking and allocating: a background thread, never an audio callback.
    /// </summary>
    float[] DecodeFile(string path, out int channels, out int sampleRate);
}

/// <summary>
///     <see cref="IAudioApi" /> over the real engine. Pure forwarding — every guard, every clamp and
///     every null-termination already lives on <see cref="ZigoteEngine" />, and duplicating them here
///     would be a second place for them to drift.
/// </summary>
internal sealed class EngineAudioApi(ZigoteEngine engine) : IAudioApi
{
    public int OutputRate => engine.AudioOutputRate();

    public int Reopen(int sampleRateHz) => engine.AudioReopen(sampleRateHz);

    public uint CreateFile(string path, bool streaming) =>
        engine.AudioSoundCreateFile(path: path, streaming: streaming);

    public uint CreateStream() => engine.AudioStreamCreate();

    public int StreamPush(uint id, ReadOnlySpan<byte> bytes) =>
        engine.AudioStreamPush(id: id, bytes: bytes);

    public void StreamFinish(uint id) => engine.AudioStreamFinish(id);

    public AudioStreamState StreamStatus(uint id) => engine.AudioStreamStatus(id);

    public float StreamBuffered(uint id) => engine.AudioStreamBuffered(id);

    public void Destroy(uint id) => engine.AudioSoundDestroy(id);

    public void Play(uint id) => engine.AudioSoundPlay(id);

    public void Stop(uint id) => engine.AudioSoundStop(id);

    public void Seek(uint id, float seconds) => engine.AudioSoundSeek(id: id, seconds: seconds);

    public void ScheduleStart(uint id, float secondsFromNow) =>
        engine.AudioSoundScheduleStart(id: id, secondsFromNow: secondsFromNow);

    public float Cursor(uint id) => engine.AudioSoundCursor(id);

    public float Duration(uint id) => engine.AudioSoundDuration(id);

    public bool IsPlaying(uint id) => engine.AudioSoundIsPlaying(id);

    public bool AtEnd(uint id) => engine.AudioSoundAtEnd(id);

    public void SetVolume(uint id, float volume) =>
        engine.AudioSoundSetVolume(id: id, volume: volume);

    public void SetRate(uint id, float rate) => engine.AudioSoundSetPitch(id: id, pitch: rate);

    public void SetSpatial(uint id, bool enabled) =>
        engine.AudioSoundSetSpatial(id: id, enabled: enabled);

    public void SetEq(uint id, uint eqId) => engine.AudioSoundSetEq(id: id, eqId: eqId);

    public uint EqCreate(int bandCount) => engine.AudioEqCreate(bandCount);

    public void EqSetBand(uint eqId, int index, AudioBandKind kind, float freqHz, float gainDb,
        float q)
    {
        engine.AudioEqSetBand(
            eqId: eqId,
            index: index,
            kind: kind,
            freqHz: freqHz,
            gainDb: gainDb,
            q: q
        );
    }

    public void EqSetEnabled(uint eqId, bool enabled) =>
        engine.AudioEqSetEnabled(eqId: eqId, enabled: enabled);

    public void EqDestroy(uint eqId) => engine.AudioEqDestroy(eqId);

    public float[] DecodeFile(string path, out int channels, out int sampleRate) =>
        engine.AudioDecodeFile(path: path, channels: out channels, sampleRate: out sampleRate);
}
