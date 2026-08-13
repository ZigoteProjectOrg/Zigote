namespace Zigote.Scripting;

/// <summary>
///     Gameplay music on top of the generic <see cref="Audio" /> API: play a track, crossfade to the
///     next, fade out on stop, and duck (e.g. while paused). Tracks are streamed, non-spatial, looping
///     by default. The host ticks the fades once per render frame and resets on play stop; outside
///     play
///     every call is a safe no-op (the underlying Audio backend is null).
/// </summary>
public static class Music
{
    private static SoundHandle _current;
    private static SoundHandle _outgoing;
    private static float _fade = 1f; // 0 → 1 crossfade position (1 = settled on _current)
    private static float _fadeSeconds;
    private static bool _stopping; // fading to silence, no incoming track

    private static float
        _duckBlend; // 0 = normal, 1 = fully ducked; lerps so DuckSeconds is the full settle time

    /// <summary>Master music volume [0,1] on top of fades and ducking.</summary>
    public static float Volume { get; set; } = 1f;

    /// <summary>Duck target while <see cref="Ducked" /> (pause menus, dialogue).</summary>
    public static float DuckVolume { get; set; } = 0.3f;

    /// <summary>Seconds the duck takes to settle in either direction.</summary>
    public static float DuckSeconds { get; set; } = 0.25f;

    /// <summary>
    ///     Duck the music toward <see cref="DuckVolume" /> (smoothly, over <see cref="DuckSeconds" />
    ///     ).
    /// </summary>
    public static bool Ducked { get; set; }

    /// <summary>
    ///     Optional mixer bus new tracks are routed through (create once via
    ///     <see cref="Audio.CreateBus" />).
    /// </summary>
    public static AudioBus Bus { get; set; } = AudioBus.None;

    /// <summary>The path passed to the last <see cref="Play" />, while a track is live.</summary>
    public static string? CurrentTrack { get; private set; }

    public static bool IsPlaying => _current.IsValid;

    /// <summary>
    ///     Start a track, crossfading from whatever is playing. Calling with the already-playing track
    ///     is a no-op (so it is safe to call from a scene's OnCreate on every scene entry).
    /// </summary>
    public static void Play(string path, float crossfadeSeconds = 1f, bool loop = true)
    {
        if (!Audio.IsAvailable) return;
        if (_current.IsValid && !_stopping && CurrentTrack == path) return;

        var incoming = Audio.CreateFile(path: path, streaming: true);
        if (!incoming.IsValid) return;
        Audio.SetSpatial(sound: incoming, enabled: false);
        Audio.SetLooping(sound: incoming, looping: loop);
        if (Bus.IsValid) Audio.SetBus(sound: incoming, bus: Bus);

        // A crossfade already in flight: the half-faded outgoing track just ends now.
        if (_outgoing.IsValid) DestroyTrack(_outgoing);

        _outgoing = _current;
        _current = incoming;
        CurrentTrack = path;
        _stopping = false;
        _fadeSeconds = MathF.Max(x: 0f, y: crossfadeSeconds);
        _fade = _fadeSeconds > 0f ? 0f : 1f;

        Audio.SetVolume(sound: incoming, volume: EffectiveVolume(_fade));
        Audio.Play(incoming);
        ApplyFade(); // settle volumes (and free the outgoing track when the fade is instant)
    }

    /// <summary>Fade the music to silence and free the track.</summary>
    public static void Stop(float fadeSeconds = 0f)
    {
        if (!_current.IsValid) return;
        if (_outgoing.IsValid) DestroyTrack(_outgoing);

        _outgoing = _current;
        _current = SoundHandle.None;
        CurrentTrack = null;
        _stopping = true;
        _fadeSeconds = MathF.Max(x: 0f, y: fadeSeconds);
        _fade = _fadeSeconds > 0f ? 0f : 1f;
        ApplyFade();
    }

    /// <summary>Advance fades + ducking. Called by the host once per render frame during play.</summary>
    public static void Tick(float dt)
    {
        float blendTarget = Ducked ? 1f : 0f;
        float blendStep = DuckSeconds > 0f ? dt / DuckSeconds : 1f;
        _duckBlend = _duckBlend < blendTarget
            ? MathF.Min(x: blendTarget, y: _duckBlend + blendStep)
            : MathF.Max(x: blendTarget, y: _duckBlend - blendStep);

        if (_fade < 1f)
            _fade = _fadeSeconds > 0f ? MathF.Min(x: 1f, y: _fade + (dt / _fadeSeconds)) : 1f;
        ApplyFade();
    }

    /// <summary>Drop every track and reset state. Called by the host on play stop (backend still live).</summary>
    public static void Reset()
    {
        if (_outgoing.IsValid) DestroyTrack(_outgoing);
        if (_current.IsValid) DestroyTrack(_current);
        _outgoing = SoundHandle.None;
        _current = SoundHandle.None;
        CurrentTrack = null;
        _stopping = false;
        _fade = 1f;
        _duckBlend = 0f;
        Ducked = false;
        Bus = AudioBus.None;
    }

    private static void ApplyFade()
    {
        if (_current.IsValid) Audio.SetVolume(sound: _current, volume: EffectiveVolume(_fade));
        if (_outgoing.IsValid)
        {
            if (_fade >= 1f)
            {
                DestroyTrack(_outgoing);
                _outgoing = SoundHandle.None;
                if (_stopping) _stopping = false;
            }
            else
                Audio.SetVolume(sound: _outgoing, volume: EffectiveVolume(1f - _fade));
        }
    }

    private static float EffectiveVolume(float fade)
    {
        float duckLevel = 1f + ((MathF.Max(x: 0f, y: DuckVolume) - 1f) * _duckBlend);
        return MathF.Max(x: 0f, y: Volume) * duckLevel * fade;
    }

    private static void DestroyTrack(SoundHandle handle)
    {
        Audio.Stop(handle);
        Audio.Destroy(handle);
    }
}
