namespace Zigote.Audioplayer;

/// <summary>
///     Something to play: a file, a region of a file, or a socket you push bytes into.
///     just_audio's <c>AudioSource</c> without the nesting — <c>ClippingAudioSource</c> is a start/end
///     pair here rather than a wrapper, because a tree of one-child decorators buys nothing when the
///     only two decorations are "clip it" and "repeat it", and repeating is already
///     <see cref="LoopMode" />.
///     <para>
///         A playlist is a list of these (<see cref="AudioPlayer.SetAudioSources" />) — there is no
///         <c>ConcatenatingAudioSource</c> type, matching where just_audio itself landed.
///     </para>
///     <para>
///         Value equality is a convenience for tests and diffing; the player matches the playing item
///         by <b>reference</b> first, so the same file twice in one queue stays two distinct entries.
///         Build each entry once and hand the same instances back when you edit the queue.
///     </para>
/// </summary>
public sealed record AudioSource
{
    private AudioSource()
    {
    }

    /// <summary>Path on disk, or null for a push stream (<see cref="Stream" />).</summary>
    public string? Path { get; private init; }

    /// <summary>Decode as it plays instead of loading the whole file up front.</summary>
    public bool Streaming { get; private init; }

    /// <summary>Where playback starts inside the file. Positions the player reports are relative to it.</summary>
    public TimeSpan Start { get; private init; }

    /// <summary>Where playback ends, or null for "the end of the file".</summary>
    public TimeSpan? End { get; private init; }

    /// <summary>
    ///     Per-item gain in dB, folded into the player's volume — this is where a ReplayGain or
    ///     normalization tag goes, so a quiet track stops being the reason someone reaches for the
    ///     volume knob mid-playlist. 0 = as recorded.
    /// </summary>
    public float GainDb { get; private init; }

    /// <summary>
    ///     Caller's metadata, carried untouched — just_audio's <c>tag</c>. This is what a queue UI binds
    ///     to, so the player never has to know what a "track" is.
    /// </summary>
    public object? Tag { get; private init; }

    /// <summary>True when this source is fed by <see cref="AudioPlayer.Push" /> rather than read from disk.</summary>
    public bool IsStream => Path is null;

    /// <summary>A whole file. Streamed by default — the right answer for anything album-length.</summary>
    public static AudioSource File(string path, bool streaming = true, float gainDb = 0f,
        object? tag = null)
    {
        return new AudioSource { Path = path, Streaming = streaming, GainDb = gainDb, Tag = tag };
    }

    /// <summary>
    ///     A region of a file — one track of a gapless rip, a preview snippet. Playback stops at
    ///     <paramref name="end" /> exactly like a shorter file would, so the queue advances there.
    /// </summary>
    public static AudioSource Clip(string path, TimeSpan start, TimeSpan? end = null,
        bool streaming = true, float gainDb = 0f, object? tag = null)
    {
        return new AudioSource
        {
            Path = path,
            Streaming = streaming,
            Start = start > TimeSpan.Zero ? start : TimeSpan.Zero,
            End = end,
            GainDb = gainDb,
            Tag = tag,
        };
    }

    /// <summary>
    ///     A source with no file behind it: hand it container bytes with <see cref="AudioPlayer.Push" />
    ///     as they arrive (an internet radio station), then <see cref="AudioPlayer.FinishStream" />.
    ///     Unseekable, and usually of unknown length.
    /// </summary>
    public static AudioSource Stream(float gainDb = 0f, object? tag = null)
    {
        return new AudioSource { GainDb = gainDb, Tag = tag };
    }
}
