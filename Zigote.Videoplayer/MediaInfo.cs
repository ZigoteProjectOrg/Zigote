namespace Zigote.Videoplayer;

/// <summary>
///     Where a <see cref="VideoPlayer" /> is in its life. One enum instead of the three independent
///     booleans (<c>isInitialized</c>, <c>isPlaying</c>, <c>isBuffering</c>) a Flutter
///     <c>VideoPlayerValue</c> makes you correlate by hand — states that cannot coexist should not be
///     representable at the same time.
/// </summary>
public enum PlaybackState
{
    /// <summary>Nothing opened yet.</summary>
    Idle,

    /// <summary>Probing the source. <see cref="VideoPlayer.Media" /> is not readable yet.</summary>
    Opening,

    /// <summary>Opened and parked at a position, not advancing.</summary>
    Ready,

    /// <summary>Frames are being presented and the clock is running.</summary>
    Playing,

    /// <summary>Paused by the caller; the clock is frozen and the pipeline is held warm.</summary>
    Paused,

    /// <summary>Wanted to play, but the decoder has not handed over enough to present.</summary>
    Buffering,

    /// <summary>Ran past the last frame and <see cref="VideoPlayer.Loop" /> is off.</summary>
    Ended,

    /// <summary>Gave up. <see cref="VideoPlayer.Error" /> says why.</summary>
    Failed,
}

/// <summary>The video stream ffprobe reported, or none for an audio-only source.</summary>
public sealed record VideoTrackInfo(int Width, int Height, double FrameRate, string Codec)
{
    public double AspectRatio => Height > 0 ? (double)Width / Height : 16.0 / 9.0;
}

/// <summary>The audio stream ffprobe reported, or none for a silent source.</summary>
public sealed record AudioTrackInfo(int Channels, int SampleRate, string Codec, string Language);

/// <summary>
///     Everything known about a source after probing it. Available as a whole the moment
///     <see cref="VideoPlayer.OpenAsync" /> returns, rather than dribbling out of a value object as
///     playback discovers it.
/// </summary>
public sealed record MediaInfo(
    string Source,
    TimeSpan Duration,
    VideoTrackInfo? Video,
    AudioTrackInfo? Audio,
    bool IsLive = false)
{
    public bool HasVideo => Video is not null;

    public bool HasAudio => Audio is not null;

    /// <summary>Display aspect of the video track; 16:9 when there is no video to ask.</summary>
    public double AspectRatio => Video?.AspectRatio ?? 16.0 / 9.0;

    /// <summary>
    ///     False for live sources and anything ffprobe could not measure. A transport bound to this
    ///     shows no scrubber rather than one over an invented length.
    /// </summary>
    public bool IsSeekable => !IsLive && Duration > TimeSpan.Zero;

    /// <summary>True when the source is opened over the network rather than off a disk.</summary>
    public bool IsNetwork => FFmpeg.IsNetwork(Source);
}
