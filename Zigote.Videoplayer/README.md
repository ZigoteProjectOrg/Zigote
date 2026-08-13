# Zigote.Videoplayer

An ffmpeg-backed video player: transport, A/V sync, a widget, and a transport bar.

```csharp
var player = new VideoPlayer(engine.Audio);
await player.OpenAsync("/media/clip.mkv", maxHeight: 720);
player.Speed.Value = 1.5;
player.Play();

new Column(children: [
    new VideoView(player, VideoFit.Contain) { AltText = "Lecture recording" },
    new VideoControls(player),
])
```

## State is signals, one per thing

`State`, `Position`, `Buffered`, `Error` and `Media` report; `Volume`, `Muted`, `Speed` and `Loop` are written to drive
the player. They are separate on purpose: a transport bar subscribes the elapsed label to
`Position` and the play glyph to `State`, so sixty position changes a second retouch one label and nothing re-measures.

```csharp
player.Position.Observe(() => elapsed.Text = Format(player.Position.Value));  // retained mutation
player.Muted.Value = true;                                                    // drives the mixer
```

`VideoControls` is the worked example: built once, six subscriptions, no rebuilds. Binding a whole subtree with `Watch`
also works and is right for state that changes on user actions rather than per frame. `Duration`, `Progress`,
`IsPlaying`, `TextureHandle` and `FrameSize` are derived reads.

## Shape

Flutter's [`video_player`](https://pub.dev/packages/video_player) is the reference, with the parts that make it awkward
removed:

| `video_player`                                 | here                                      |
|------------------------------------------------|-------------------------------------------|
| `initialize()` then poll `value.isInitialized` | `OpenAsync` returns the full `MediaInfo`  |
| `isPlaying` + `isBuffering` + `isInitialized`  | one `PlaybackState`                       |
| `value.errorDescription`, a string on a struct | an `Error` signal, and a thrown exception |
| `setPlaybackSpeed` / `setVolume` are `Future`s | `Speed` / `Volume` are signals you write  |
| aspect only after init, `AspectRatio` by hand  | `VideoView` sizes and letterboxes itself  |
| controller must be built per source            | `OpenAsync` again, same player            |

Every call is safe in every state — there is no "not initialized yet" window to guard.

## How it plays

Two `ffmpeg` child processes per pipeline, both started at the same media offset:

```text
  ffmpeg -f rawvideo -pix_fmt rgba ─→ [frame ring] ─→ Tick() ─→ texel overwrite ─→ VideoView
  ffmpeg -f s16le                  ─→ WAV header + PCM ─→ IAudioApi push stream ─→ mixer
```

Both emit at a fixed rate, so **output frame `N` is at output second `N / fps`** — no timestamps to parse out of a raw
stream. The mixer's delivered-sample count is the master clock; video is presented against it and frames it is late for
are dropped. With no audio track (or no device) the wall clock stands in, and it takes over anyway if the mixer's cursor
turns out not to move.

Opening warms the pipeline at position zero on the next tick, so a freshly opened video shows its first frame instead of
a black rectangle and the first `Play()` is instant.

Seeking and changing `Speed` are the same operation: tear the pipeline down and start it again at the target offset.
`-ss` before `-i` makes that a keyframe jump plus a decode-forward, and doing it one way means the two streams cannot
drift apart. Speed is `setpts` on the video and chained
`atempo` on the audio, so 2× speech is still speech — `IAudioApi.SetRate` is varispeed (pitch follows rate), which is
the wrong tool for a transport control.

Driving ffmpeg's CLI rather than linking `libav*` buys the entire format and codec matrix — every container,
http/rtsp/rtmp inputs, hardware decoders — against a command line that has been stable for a decade, instead of against
struct layouts that shift with each major soname. It costs one process per stream and a pipe copy per frame.

### The frame path allocates nothing

Steady state is: read a pipe into a recycled buffer, then overwrite the texture's texels.

`ArrayPool<byte>.Shared` is not used — it declines anything over 1 MB, and every frame from 540p up is bigger than that,
so it would hand back a fresh multi-megabyte array per frame. Each pipeline keeps its own free list instead; every
buffer is exactly one frame, so that is the whole data structure. The ring is sized by `TargetBufferSeconds` and capped
by `MaxBufferBytes` (64 MB default)
— the cap is what does the work above 720p, where a second of decoded RGBA is hundreds of megabytes.

On the GPU side `ZigoteEngine.UpdateTextureRgba` rewrites the existing texture rather than building a new one:
`zigote_update_texture_rgba` copies into the image registry from any thread and queues a
`writeTexture` that the render thread drains at the top of the next frame, keeping the texture, its view and its bind
group. Only the first frame of a source allocates any of that. A width that is already a multiple of 64 px (1280, 1920,

3840) needs no row padding and uploads straight from the caller's buffer.

### Streaming

Any URL ffmpeg can open — http (s), HLS, DASH, rtsp, rtmp, srt. http (s) sources additionally get
`-reconnect`, `-reconnect_streamed`, `-reconnect_on_network_error`, a bounded backoff and persistent connections, so a
dropped edge or a 5xx resumes with a range request instead of ending the video. They are protocol options, so they are
emitted only for the schemes that accept them.

Both pipes pin `-map 0:v:0` / `-map 0:a:0?`. A multi-variant HLS master exposes every rendition as its own stream and
ffmpeg's default pick is the largest — not the one `ParseProbe` measured, which would leave the reader slicing raw
frames at the wrong size.

A network source with no measurable duration is `IsLive`: not seekable, no scrubber over an invented length.

`Buffered` reports the decoded read-ahead: queued frames, or the mixer's undrained audio, whichever is smaller —
playback stops when either empties. It is decoded read-ahead, not downloaded; ffmpeg's own network buffer sits in front
of it and is not visible from here.

**Nothing runs on its own.** The host calls `Tick()` once a frame; `VideoView` does it while it is on screen.

## Requires

`ffmpeg` and `ffprobe` on `PATH`, or `ZIGOTE_FFMPEG` / `ZIGOTE_FFPROBE` pointing at a build. Call
`FFmpeg.IsAvailable()` at startup so a missing install is your own error message rather than a failed open later.

## Not here

- **Hardware decode surfaces.** Frames come back as RGBA through a pipe: one copy from the pipe and one into the
  texture, no allocation. A 1080p60 source is still ~500 MB/s of upload. Removing the remaining copies means decoding
  into a GPU surface the renderer can sample directly, which is a decoder-and-engine change, not a player one.
- **Adaptive bitrate.** ffmpeg follows one HLS/DASH rendition; nothing here switches renditions as bandwidth moves.
  Re-open at a different `maxHeight` to change quality.
- **Track selection and subtitles.** One video and one audio stream, ffmpeg's default pick. Both are
  `-map` arguments away when something needs them.
- **A frame callback.** `TextureHandle` is the frame; read it in a paint pass.
