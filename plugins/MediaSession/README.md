# MediaSession

Lock-screen, notification-shade, headset, car and media-key controls — the `audio_service` slot
from the [plugin roadmap](../../docs/plugin-roadmap.md). Transport only: the app says what is
playing and acts on the commands that come back; which player makes the sound is not this
plugin's business.

```csharp
var media = new MediaSessionClient(appId: "dev.zigote.MyApp", appName: "My App");
await media.StartAsync();                     // never throws; nothing to talk to = Active false

media.Command += command => App.Active?.Post(() => // OS thread — hop to the UI thread first
{
    switch (command.Action)
    {
        case MediaAction.Play: player.Play(); break;
        case MediaAction.Pause or MediaAction.Stop: player.Pause(); break;
        case MediaAction.PlayPause: player.TogglePlayPause(); break;
        case MediaAction.Next: player.SeekToNext(); break;
        case MediaAction.Previous: player.SeekToPrevious(); break;
        case MediaAction.SeekTo: player.Seek(command.Position); break;   // always absolute
        case MediaAction.Raise: window.Show(); break;                    // desktop only
    }
});

// On every state change, track change and seek — not every frame; surfaces extrapolate.
media.Update(new NowPlaying(title) {
    Artist = artist, Album = album, ArtPath = coverFile,
    Duration = player.Duration.Value ?? TimeSpan.Zero,    // zero = live stream, no seek bar
    Position = player.Position.Value, Playing = player.IsPlaying,
    CanNext = player.HasNext, CanPrevious = player.HasPrevious,
});
media.Update(null);                           // nothing loaded: the surfaces clear
```

| Platform | Backend | Notes |
|---|---|---|
| Linux | MPRIS2 over D-Bus (`org.mpris.MediaPlayer2.<appId>`) | GNOME/KDE media widgets, media keys, `playerctl`. Lifted from Timbre. A second instance can't take the name and stays inactive. |
| Android | framework `MediaSession` + media-style notification + `mediaPlayback` foreground service | Also pauses on audio-focus loss and on "headphones unplugged", and resumes after a transient loss. The service starts on the first `Playing` update and stops on `Update(null)`. Manifest entries merge in automatically; ask for `POST_NOTIFICATIONS` on 13+ through the Permissions plugin. |
| iOS | `MPNowPlayingInfoCenter` + `MPRemoteCommandCenter` | Sets the audio session to `Playback`. Background audio still needs `UIBackgroundModes = audio` in Info.plist. |
| Windows / macOS | not yet — `Active` is false and every call no-ops | SMTC and the macOS Now Playing widget are future work. |

Not covered: volume, shuffle and repeat. MPRIS reports them as fixed, and Android and iOS don't
send them. Add them when an app needs the system to change them.
