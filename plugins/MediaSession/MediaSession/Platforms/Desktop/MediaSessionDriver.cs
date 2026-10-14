namespace MediaSession;

/// <summary>
///     Desktop implementation: MPRIS2 over D-Bus on Linux; a no-op on Windows (SMTC needs a window
///     handle and WinRT) and macOS (MPNowPlayingInfoCenter from an unbound process) — future work,
///     and <see cref="Active" /> says so.
/// </summary>
internal sealed class MediaSessionDriver : IDisposable
{
    private readonly Mpris? _mpris;

    public MediaSessionDriver(string appId, string appName, Action<MediaCommand> raise)
    {
        if (OperatingSystem.IsLinux()) _mpris = new Mpris(appId, appName, raise);
    }

    public bool Active => _mpris?.Running ?? false;

    public Task StartAsync() => _mpris?.StartAsync() ?? Task.CompletedTask;

    public void Update(NowPlaying? nowPlaying) => _mpris?.Update(nowPlaying);

    public void Seeked(TimeSpan position) => _mpris?.NotifySeeked(position);

    public void Dispose() => _mpris?.Dispose();
}
