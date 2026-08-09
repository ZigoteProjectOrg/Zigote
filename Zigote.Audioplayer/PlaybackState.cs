namespace Zigote.Audioplayer;

/// <summary>
///     Where an <see cref="AudioPlayer" /> is. One enum rather than just_audio's
///     <c>playing</c> + <c>processingState</c> pair: every combination a caller actually branches on
///     is a state here, and the ones that cannot coexist are not representable.
///     <para>
///         Deliberately the same states, with the same names, as
///         <c>Zigote.Videoplayer.PlaybackState</c> — the two players are read by the same eyes and
///         bound by the same transport bars.
///         <!-- ponytail: duplicated rather than shared, because the shared home would have to be
///              Zigote.Core and moving the video player's public enum breaks its consumers. Merge
///              the two the day one app binds both to one control. -->
///     </para>
/// </summary>
public enum PlaybackState
{
    /// <summary>Nothing loaded: a fresh player, an empty queue, or after <see cref="AudioPlayer.Stop" />.</summary>
    Idle,

    /// <summary>A source is open but not yet playable — a stream whose container has not been identified.</summary>
    Opening,

    /// <summary>Loaded and parked at a position, never started. The state a queue sits in before the first play.</summary>
    Ready,

    /// <summary>The cursor is moving.</summary>
    Playing,

    /// <summary>Parked by the caller. The decoder stays open at the position it held.</summary>
    Paused,

    /// <summary>Wanted to play, but the stream has nothing decoded left. The spinner state.</summary>
    Buffering,

    /// <summary>The queue ran out with <see cref="LoopMode.All" /> off. <see cref="AudioPlayer.Play" /> replays.</summary>
    Ended,

    /// <summary>Gave up on this item. <see cref="AudioPlayer.Error" /> says why.</summary>
    Failed,
}

/// <summary>What repeats when an item ends. just_audio's <c>LoopMode</c>.</summary>
public enum LoopMode
{
    /// <summary>Stop after the last item.</summary>
    Off,

    /// <summary>Repeat the current item forever.</summary>
    One,

    /// <summary>Wrap around to the first item.</summary>
    All,
}
