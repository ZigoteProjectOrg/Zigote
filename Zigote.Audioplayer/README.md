# Zigote.Audioplayer

A media player over `IAudioApi`: queue, transport, gapless advance, equalizer.

```csharp
var player = new AudioPlayer(engine.Audio);
player.SetAudioSources([
    AudioSource.File("intro.flac", gainDb: -3.2f),                       // ReplayGain tag
    AudioSource.Clip("side-a.flac", start, end, tag: track),             // one track of a rip
    AudioSource.Stream(tag: station),                                    // push bytes with Push()
]);
player.Loop.Value = LoopMode.All;
player.Equalizer = Equalizer.TenBand(engine.Audio);
player.Play();

player.Tick();   // once a frame — the player has no thread of its own
```

## State is signals, one per thing

`State`, `Position`, `BufferedPosition`, `Duration`, `CurrentIndex`, `Sequence` and `Error` report;
`Volume`, `Muted`, `Speed`, `Loop` and `Shuffle` are written to drive the player. Separate on purpose, and retained-mode
on purpose: a transport bar subscribes the elapsed label to `Position`
and the play glyph to `State`, so sixty position changes a second retouch one label and nothing re-measures, nothing
rebuilds, and a queue list keeps its scroll offset across a track change.

```csharp
player.Position.Observe(() => elapsed.Text = Format(player.Position.Value));  // retained mutation
player.Muted.Value = true;                                                    // drives the mixer
```

Bind a whole subtree with `Watch` only where the state changes on user actions rather than per frame — a now-playing
panel keyed on `CurrentIndex` is fine; the seek bar is not. `Current`,
`IsPlaying`, `Progress`, `EffectiveIndices`, `NextIndex`/`PreviousIndex` and `HasNext`/`HasPrevious`
are derived reads, not signals — they cost nothing and notify nobody.

## Shape

[just_audio](https://pub.dev/packages/just_audio) is the API reference, with what the neighbours do better folded in:

| elsewhere                                            | here                                                     |
|------------------------------------------------------|----------------------------------------------------------|
| `playing` + `processingState` to correlate           | one `PlaybackState`, same states as `Zigote.Videoplayer` |
| `ConcatenatingAudioSource`, `ClippingAudioSource`, … | a list of `AudioSource`; clipping is a start/end pair    |
| `setVolume`/`setSpeed` are futures                   | `Volume` / `Speed` are signals you write                 |
| ExoPlayer's `setMaxSeekToPreviousPosition`           | `MaxSeekToPreviousPosition`, 3 s, same default           |
| a `PlayerException` thrown at a listener             | an `Error` signal and the `Failed` state                 |
| a dead file halts the queue                          | it is skipped; a queue of dead files fails once          |

Ideas the sources do not cover, taken from what breaks in the field: the position bar never snaps backwards on a stale
post-seek cursor, rebuffering has hysteresis so a marginal stream cannot flicker Playing/Buffering once a frame,
per-item `GainDb` folds ReplayGain in, the shuffle order is seeded once so editing the queue does not deal a new one,
and the same file twice in one queue stays two entries.

`AudioPlayer` talks to **nothing but `IAudioApi`**, so all of that — queue, transport, clipping, gapless, buffering —
runs in CI against a fake device. See `Zigote.Tests/AudioPlayerTests.cs`.

## Gapless

`Tick` arms the next item `GaplessLead` (2 s) before the current one runs out: it opens the file, seeks it to its start,
and hands the mixer a start time on the audio clock via `ScheduleStart`. Polling can never be tighter than a frame; the
audio thread can hit the sample. When the item ends, the armed sound is *adopted* — promoted in place, not started — so
nothing restarts and no frame boundary is audible. Pausing, seeking, or changing `Speed` disarms it (the hand-off was
scheduled in wall-clock seconds at the old rate), and the next `Tick` re-arms.

## Not here

- **Pitch-preserving speed.** `Speed` is varispeed — the engine resamples, so pitch follows rate. A time-stretcher
  belongs in the engine next to the resampler, not in a caller-side trick.
- **Crossfade.** `Zigote.Scripting.Music` already does that for gameplay tracks; this module is gapless, which is the
  opposite requirement.
- **Async open.** `CreateFile` parses the container header on the calling thread (see the
  `ponytail:` note in `AudioPlayer.Load`). `Zigote.Core.Threading.Background` is the upgrade when a slow disk shows up
  in a frame trace.
- **Metadata, artwork, ICY tags.** Read them yourself and hang them on `AudioSource.Tag`.
- **A transport-bar widget.** `Zigote.Videoplayer.VideoControls` is the worked example to copy; this module stays on
  `Zigote.Core` so a headless host can queue audio without pulling in the UI.
