namespace MediaSession;

/// <summary>
///     What the system media surfaces show: the lock screen, the notification shade, a headset,
///     a car, GNOME's media widget. Publish a new one whenever any of it changes — the drivers diff.
/// </summary>
/// <param name="Title">The one field every surface draws.</param>
public sealed record NowPlaying(string Title)
{
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }

    /// <summary>1-based; 0 for none.</summary>
    public int TrackNumber { get; init; }

    /// <summary>Cover art as a local file. Remote art is the app's to download first.</summary>
    public string? ArtPath { get; init; }

    /// <summary>Zero for a live stream: no seek bar, and seeking is not offered.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    ///     Where playback is. Surfaces extrapolate from this while <see cref="Playing" />, so it
    ///     only needs publishing on a state change or a seek — not every frame.
    /// </summary>
    public TimeSpan Position { get; init; }

    public bool Playing { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }

    /// <summary>
    ///     Same track as <paramref name="other" /> — the identity the drivers key track ids and
    ///     artwork reloads off. Position and play state are not part of it.
    /// </summary>
    internal bool SameItem(NowPlaying? other) =>
        other is not null && Title == other.Title && Artist == other.Artist && Album == other.Album
        && Duration == other.Duration && ArtPath == other.ArtPath;

    /// <summary>
    ///     A relative seek turned absolute, clamped to the track. The surfaces that send "+10 s"
    ///     (MPRIS <c>Seek</c>) never know the duration as well as the app does.
    /// </summary>
    internal TimeSpan SeekTarget(TimeSpan offset)
    {
        var target = Position + offset;
        if (target < TimeSpan.Zero) return TimeSpan.Zero;
        return Duration > TimeSpan.Zero && target > Duration ? Duration : target;
    }
}

/// <summary>What the system asked for.</summary>
public enum MediaAction
{
    Play,
    Pause,
    PlayPause,

    /// <summary>Stop and rewind — or just pause; the app decides what stopping means.</summary>
    Stop,
    Next,
    Previous,

    /// <summary>Jump to <see cref="MediaCommand.Position" /> — always absolute; relative seeks are resolved for you.</summary>
    SeekTo,

    /// <summary>The user clicked the media widget and wants the app's window. Desktop only.</summary>
    Raise
}

/// <param name="Position">Only meaningful for <see cref="MediaAction.SeekTo" />.</param>
public readonly record struct MediaCommand(MediaAction Action, TimeSpan Position = default);

/// <summary>
///     MediaSession — the <c>audio_service</c> slot from the plugin roadmap. Transport only: the
///     app publishes <see cref="NowPlaying" /> and acts on <see cref="Command" />; which player
///     plays the audio is none of this plugin's business.
///     <para>
///         On Android this also owns what background playback needs to survive: audio focus, the
///         "headphones unplugged" pause, and the foreground service with its media notification.
///     </para>
/// </summary>
public sealed class MediaSessionClient : IDisposable
{
    private readonly MediaSessionDriver _driver;

    /// <param name="appId">Reverse-DNS id — the Linux <c>.desktop</c> entry and the Android notification channel.</param>
    /// <param name="appName">What the system calls the player.</param>
    public MediaSessionClient(string appId, string appName)
        => _driver = new MediaSessionDriver(appId, appName, command => Command?.Invoke(command));

    /// <summary>
    ///     A transport command from the system. Raised on the OS's thread (D-Bus, binder, main
    ///     run loop) — post to the UI thread before touching widgets, or write a Signal.
    /// </summary>
    public event Action<MediaCommand>? Command;

    /// <summary>True once the system can see this player. False on Windows and macOS for now, and on Linux without a session bus.</summary>
    public bool Active => _driver.Active;

    /// <summary>Register with the system. Never throws: no media service is a normal answer.</summary>
    public Task StartAsync() => _driver.StartAsync();

    /// <summary>What is playing now, or null when nothing is loaded (the surfaces clear).</summary>
    public void Update(NowPlaying? nowPlaying) => _driver.Update(nowPlaying);

    /// <summary>
    ///     The app seeked by itself (a scrub in its own UI). Publish the new <see cref="NowPlaying" />
    ///     as well — this is the separate event MPRIS clients resync their progress bars from.
    /// </summary>
    public void Seeked(TimeSpan position) => _driver.Seeked(position);

    public void Dispose() => _driver.Dispose();
}
