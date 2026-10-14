using AVFoundation;
using Foundation;
using MediaPlayer;
using UIKit;

namespace MediaSession;

/// <summary>
///     iOS implementation — <c>MPNowPlayingInfoCenter</c> for what the lock screen and Control
///     Centre draw, <c>MPRemoteCommandCenter</c> for the buttons, headset clicks and CarPlay.
///     <para>
///         iOS only shows controls for an app whose audio session is in a playback category, so
///         <see cref="StartAsync" /> sets one. Background audio additionally needs
///         <c>UIBackgroundModes = audio</c> in the app's Info.plist — a build-time fact this plugin
///         cannot add.
///     </para>
/// </summary>
internal sealed class MediaSessionDriver : IDisposable
{
    private readonly Action<MediaCommand> _raise;
    private readonly List<(MPRemoteCommand Command, NSObject Token)> _targets = [];
    private string? _artPath;
    private MPMediaItemArtwork? _art;

    public MediaSessionDriver(string appId, string appName, Action<MediaCommand> raise) => _raise = raise;

    public bool Active { get; private set; }

    public Task StartAsync()
    {
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback);
                AVAudioSession.SharedInstance().SetActive(true);

                var center = MPRemoteCommandCenter.Shared;
                On(center.PlayCommand, _ => _raise(new MediaCommand(MediaAction.Play)));
                On(center.PauseCommand, _ => _raise(new MediaCommand(MediaAction.Pause)));
                On(center.TogglePlayPauseCommand, _ => _raise(new MediaCommand(MediaAction.PlayPause)));
                On(center.StopCommand, _ => _raise(new MediaCommand(MediaAction.Stop)));
                On(center.NextTrackCommand, _ => _raise(new MediaCommand(MediaAction.Next)));
                On(center.PreviousTrackCommand, _ => _raise(new MediaCommand(MediaAction.Previous)));
                On(center.ChangePlaybackPositionCommand, e =>
                {
                    if (e is MPChangePlaybackPositionCommandEvent seek)
                        _raise(new MediaCommand(MediaAction.SeekTo, TimeSpan.FromSeconds(seek.PositionTime)));
                });
                Active = true;
            }
            catch (Exception)
            {
                // No session category, no lock-screen controls; playback is unaffected.
            }
        });
        return Task.CompletedTask;
    }

    private void On(MPRemoteCommand command, Action<MPRemoteCommandEvent> handler)
    {
        var token = command.AddTarget(e =>
        {
            handler(e);
            return MPRemoteCommandHandlerStatus.Success;
        });
        _targets.Add((command, token));
    }

    public void Update(NowPlaying? nowPlaying) => UIApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        var center = MPRemoteCommandCenter.Shared;
        center.NextTrackCommand.Enabled = nowPlaying?.CanNext ?? false;
        center.PreviousTrackCommand.Enabled = nowPlaying?.CanPrevious ?? false;
        center.ChangePlaybackPositionCommand.Enabled = nowPlaying is { Duration.Ticks: > 0 };

        if (nowPlaying is null)
        {
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo();
            MPNowPlayingInfoCenter.DefaultCenter.PlaybackState = MPNowPlayingPlaybackState.Stopped;
            return;
        }

        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo
        {
            Title = nowPlaying.Title,
            Artist = nowPlaying.Artist,
            AlbumTitle = nowPlaying.Album,
            AlbumArtist = nowPlaying.AlbumArtist,
            AlbumTrackNumber = nowPlaying.TrackNumber > 0 ? nowPlaying.TrackNumber : null,
            PlaybackDuration = nowPlaying.Duration > TimeSpan.Zero ? nowPlaying.Duration.TotalSeconds : null,
            IsLiveStream = nowPlaying.Duration == TimeSpan.Zero,
            ElapsedPlaybackTime = nowPlaying.Position.TotalSeconds,
            // The lock screen extrapolates the position from rate × elapsed; 0 freezes it.
            PlaybackRate = nowPlaying.Playing ? 1.0 : 0.0,
            Artwork = Art(nowPlaying.ArtPath)
        };
        MPNowPlayingInfoCenter.DefaultCenter.PlaybackState =
            nowPlaying.Playing ? MPNowPlayingPlaybackState.Playing : MPNowPlayingPlaybackState.Paused;
    });

    /// <summary>Elapsed time rides in NowPlaying, which the app republishes after a seek.</summary>
    public void Seeked(TimeSpan position)
    {
    }

    /// <summary>Artwork loaded once per path; the handler hands back the same image at any size.</summary>
    private MPMediaItemArtwork? Art(string? path)
    {
        if (path == _artPath) return _art;
        _artPath = path;
        _art = null;
        if (string.IsNullOrEmpty(path) || UIImage.FromFile(path) is not { } image) return null;
        _art = new MPMediaItemArtwork(image.Size, _ => image);
        return _art;
    }

    public void Dispose() => UIApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        foreach (var (command, token) in _targets) command.RemoveTarget(token);
        _targets.Clear();
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo();
        Active = false;
    });
}
