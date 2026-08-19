using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Zigote.Core.Engine;
using Zigote.Core.State;

namespace Zigote.Videoplayer;

/// <summary>
///     A video player: an ffmpeg decode pipeline behind a transport, presenting frames into a GPU
///     texture that <see cref="VideoView" /> paints and audio into the engine's mixer.
///     <para>
///         Flutter's <c>video_player</c> in C#, with the parts that make it awkward removed.
///         <see cref="OpenAsync" /> hands back a complete <see cref="MediaInfo" /> instead of leaving
///         you to poll a value object for <c>isInitialized</c>; <see cref="State" /> is one signal
///         rather than three booleans to correlate; errors are a typed <see cref="Error" /> signal
///         rather than a string on a struct; and <see cref="Speed" />, <see cref="Loop" /> and
///         <see cref="Muted" /> are signals you write, not futures you await. Every call is safe in
///         every state — there is no "not initialized yet" window to guard.
///     </para>
///     <para>
///         State is fine-grained <see cref="Signal{T}" />s, matching <c>Zigote.Audioplayer</c>. Each
///         one is separately observable, so a transport bar can bind the position label to
///         <see cref="Position" /> alone and leave the rest of the tree untouched sixty times a
///         second — the read-only signals report, and writing <see cref="Volume" />,
///         <see cref="Muted" />, <see cref="Speed" /> or <see cref="Loop" /> is how you drive it.
///     </para>
///     <para>
///         <b>Nothing here runs on its own.</b> The host calls <see cref="Tick" /> once per frame —
///         that is when the clock is read, the due frame is uploaded and the end is noticed.
///         <see cref="VideoView" /> does it for you while it is on screen; a headless host does it
///         from its own loop.
///     </para>
///     <para>
///         <b>Timing comes from the audio.</b> Both pipes start at the same media offset and emit a
///         fixed-rate stream, so output frame <c>N</c> is at output second <c>N / fps</c>; the mixer's
///         delivered-sample count is the master clock and video is presented against it, dropping
///         frames it is late for. With no audio track the wall clock stands in. Seeking and changing
///         <see cref="Speed" /> both restart the pipeline at the current position — one path, so
///         neither can drift the two apart.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var player = new VideoPlayer(engine.Audio);
/// await player.OpenAsync("/media/clip.mkv");
/// player.Speed.Value = 1.5;
/// player.Play();
/// 
/// // one label, bound to one signal — see VideoControls for the whole transport
/// var elapsed = new Text("0:00");
/// player.Position.Observe(() =&gt; elapsed.Text = $"{player.Position.Value:mm\\:ss}");
/// </code>
/// </example>
public sealed class VideoPlayer : IDisposable
{
    /// <summary>
    ///     How much decoded video to hold ahead of presentation, in seconds — the read-ahead that
    ///     rides out a network stall or a slow decode. Bounded in bytes as well by
    ///     <see cref="MaxBufferBytes" />, because seconds of <i>decoded RGBA</i> is an expensive unit:
    ///     at 1080p one second is half a gigabyte.
    /// </summary>
    private const double TargetBufferSeconds = 2.0;

    /// <summary>Never fewer than this many frames, whatever the byte cap works out to.</summary>
    private const int MinFrameQueueDepth = 3;

    /// <summary>Presentation is early by up to half a frame rather than late by up to a whole one.</summary>
    private const double PresentSlackFrames = 0.5;

    /// <summary>Decoded audio to gather before starting the clock — the anti-stutter margin.</summary>
    private const float PrerollSeconds = 0.2f;

    /// <summary>
    ///     How long the audio cursor may sit still while the wall clock runs before the audio clock is
    ///     abandoned. A device that reports a stuck cursor would otherwise freeze video forever, which
    ///     is a worse failure than the slight drift of timing off the wall.
    /// </summary>
    private const double AudioClockStallSeconds = 1.0;

    private readonly IAudioApi? _audio;
    private readonly Signal<TimeSpan> _buffered = new(TimeSpan.Zero);
    private readonly Signal<string?> _error = new(null);
    private readonly Signal<MediaInfo?> _media = new(null);
    private readonly Signal<TimeSpan> _position = new(TimeSpan.Zero);
    private readonly Signal<PlaybackState> _state = new(PlaybackState.Idle);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Stopwatch _wall = new();

    private bool _disposed;
    private Frame? _held;

    /// <summary>Media seconds; authoritative while no pipeline is running, recomputed while one is.</summary>
    private double _mediaTime;

    private Pipeline? _pipe;

    /// <summary>Set once the audio cursor is proven unusable; never re-tried for this pipeline.</summary>
    private bool _useWallClock;

    /// <summary>
    ///     The caller asked to play. Distinct from <see cref="State" />, which also reports
    ///     buffering.
    /// </summary>
    private bool _wantPlay;

    /// <param name="audio">
    ///     The mixer sound goes to. Null plays silently — right for a background loop, and for a test
    ///     or a headless host with no device.
    /// </param>
    public VideoPlayer(IAudioApi? audio = null)
    {
        _audio = audio;

        // The settable signals drive the player rather than mirroring it: writing one is how you
        // change volume or rate, and the player reacts here. That keeps a bound control and a
        // keyboard shortcut on exactly the same path.
        _subscriptions.Add(Volume.Subscribe(_ => ApplyVolume()));
        _subscriptions.Add(Muted.Subscribe(_ => ApplyVolume()));
        _subscriptions.Add(
            Speed.Subscribe(_ =>
                {
                    if (_pipe is not null) Restart(_mediaTime);
                }
            )
        );
    }

    /// <summary>Where the player is. The whole transport UI can bind to just this.</summary>
    public IReadableSignal<PlaybackState> State => _state;

    /// <summary>Playback position in media time — unaffected by <see cref="Speed" />.</summary>
    public IReadableSignal<TimeSpan> Position => _position;

    /// <summary>
    ///     How far past <see cref="Position" /> playback is already decoded and ready — the pale bar
    ///     under a seek bar, and the number that says whether a stall is coming. It is the smaller of
    ///     the two read-aheads, video frames and mixer-side audio, because playback stops when either
    ///     runs dry.
    ///     <para>
    ///         Decoded read-ahead, not downloaded: ffmpeg's own network buffer sits in front of this
    ///         and is not visible from here. Seconds of decoded RGBA are expensive (see
    ///         <see cref="MaxBufferBytes" />), so expect a second or two, not the half-minute a
    ///         browser shows.
    ///     </para>
    /// </summary>
    public IReadableSignal<TimeSpan> Buffered => _buffered;

    /// <summary>
    ///     Ceiling on the decoded-frame ring, in bytes (default 64 MB). The knob that trades memory
    ///     for stall tolerance: a 540p stream on a flaky link wants more, a wall of thumbnails wants
    ///     less. Takes effect on the next pipeline start — a seek, a speed change, or the next
    ///     <see cref="OpenAsync" />.
    /// </summary>
    public long MaxBufferBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Why the last open or playback attempt failed, or null. Cleared by the next open.</summary>
    public IReadableSignal<string?> Error => _error;

    /// <summary>What was probed, or null before the first successful <see cref="OpenAsync" />.</summary>
    public IReadableSignal<MediaInfo?> Media => _media;

    /// <summary>
    ///     Linear gain, 0–1 (clamped when applied). Independent of <see cref="Muted" />, which does
    ///     not overwrite it. Write it — <c>player.Volume.Value = 0.4f</c> — and bind to it.
    /// </summary>
    public Signal<float> Volume { get; } = new(1f);

    /// <summary>Silence without losing the <see cref="Volume" /> the user had set.</summary>
    public Signal<bool> Muted { get; } = new(false);

    /// <summary>
    ///     Playback rate, 0.25×–4× (clamped when applied). Audio is time-stretched, not pitch-shifted,
    ///     so speech at 2× stays speech. Changing it restarts the pipeline at the current position,
    ///     which costs the same re-buffer as a seek.
    /// </summary>
    public Signal<double> Speed { get; } = new(1.0);

    /// <summary>Restart from the beginning at the end instead of stopping.</summary>
    public Signal<bool> Loop { get; } = new(false);

    /// <summary>Source length; <see cref="TimeSpan.Zero" /> for a live stream or an unmeasurable one.</summary>
    public TimeSpan Duration => _media.Value?.Duration ?? TimeSpan.Zero;

    /// <summary>Fraction of the way through, 0–1. Zero when the duration is unknown.</summary>
    public double Progress =>
        Duration > TimeSpan.Zero
            ? Math.Clamp(
                value: _position.Value.TotalSeconds / Duration.TotalSeconds,
                min: 0,
                max: 1
            )
            : 0;

    /// <summary>Whether the transport is advancing — buffering counts, since the intent is to play.</summary>
    public bool IsPlaying =>
        _state.Value is PlaybackState.Playing or PlaybackState.Buffering;

    /// <summary>
    ///     The current frame's GPU texture, or 0 before the first frame. Owned by the player — paint
    ///     it, never release it. <see cref="VideoView" /> is the ordinary way to show it; this is here
    ///     for callers compositing the frame themselves.
    /// </summary>
    public ulong TextureHandle { get; private set; }

    /// <summary>Pixel size of the decoded frame (post-downscale), 0×0 before the first one.</summary>
    public (uint Width, uint Height) FrameSize { get; private set; }

    /// <summary>
    ///     Frames presented so far. The repaint signal for views: <see cref="TextureHandle" /> is
    ///     stable across a pipeline (frames are texel overwrites into the same texture), so "did a
    ///     new frame arrive" must be asked of this counter, never of the handle — a repaint gated
    ///     on the handle freezes a damage-tracked scene on the first frame.
    /// </summary>
    public long FramesPresented { get; private set; }

    /// <summary>Height cap requested at <see cref="OpenAsync" />; 0 = native.</summary>
    private int MaxHeight { get; set; }

    /// <summary>Tear down the pipeline, the texture and the audio stream. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
        StopPipeline();

        if (TextureHandle != 0 && ZigoteEngine.Instance is not null)
            ZigoteEngine.ReleaseTexture(TextureHandle);
        TextureHandle = 0;
        FrameSize = (0, 0);
    }

    /// <summary>
    ///     Probe <paramref name="source" /> — a path, or any URL ffmpeg can open (http, rtsp, rtmp,
    ///     udp) — and park at position zero, ready but not playing. Replaces whatever was open.
    ///     <para>
    ///         The probe is a child process and a container parse, so this is genuinely async; unlike
    ///         Flutter's <c>initialize()</c>, failure surfaces as a thrown exception here <i>and</i>
    ///         on the <see cref="Error" /> signal, so it can be handled at either end.
    ///     </para>
    /// </summary>
    /// <param name="maxHeight">
    ///     Cap the decoded height, preserving aspect (0 = source resolution). Every frame is copied
    ///     through a pipe and uploaded whole, so a 4K source shown in a 720p pane costs 8× the
    ///     bandwidth for nothing — pass the pane's height when it is much smaller than the source.
    /// </param>
    public async Task<MediaInfo> OpenAsync(
        string source,
        int maxHeight = 0,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);

        StopPipeline();
        _media.Value = null;
        _error.Value = null;
        _wantPlay = false;
        SetPosition(0);
        MaxHeight = Math.Max(val1: 0, val2: maxHeight);
        _state.Value = PlaybackState.Opening;

        try
        {
            _media.Value = await FFmpeg.ProbeAsync(source: source, ct: ct).ConfigureAwait(false);
            _state.Value = PlaybackState.Ready;
            return _media.Value;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            throw;
        }
    }

    /// <summary>
    ///     Start or resume. From <see cref="PlaybackState.Ended" /> this rewinds first, so the play
    ///     button on a finished video does the obvious thing instead of nothing.
    /// </summary>
    public void Play()
    {
        if (_disposed || _media.Value is null || _state.Value is PlaybackState.Failed) return;

        if (_state.Value == PlaybackState.Ended) SetPosition(0);
        _wantPlay = true;

        if (_pipe is null)
        {
            Restart(_mediaTime);
            return;
        }

        ResumeClock();
        _state.Value = PlaybackState.Playing;
    }

    /// <summary>
    ///     Freeze the clock, keeping the pipeline warm so resuming is instant. ffmpeg stalls on its
    ///     own once the frame queue and the audio buffer stop draining.
    /// </summary>
    public void Pause()
    {
        if (_disposed || !_wantPlay) return;
        _wantPlay = false;
        PauseClock();
        _state.Value = PlaybackState.Paused;
    }

    /// <summary>Play if paused, pause if playing — the space bar, in one call.</summary>
    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    /// <summary>Pause and rewind to zero, releasing the decoders.</summary>
    public void Stop()
    {
        if (_disposed) return;
        _wantPlay = false;
        StopPipeline();
        SetPosition(0);
        if (_media.Value is not null && _state.Value is not PlaybackState.Failed)
            _state.Value = PlaybackState.Ready;
    }

    /// <summary>
    ///     Jump to <paramref name="position" />, clamped to the source. Restarts the decoders at the
    ///     nearest keyframe before the target and decodes forward, so the frame that appears is the
    ///     one asked for; playing across a seek keeps playing, and seeking while paused presents that
    ///     frame and stays paused.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        if (_disposed || _media.Value is null) return;

        double target = position.TotalSeconds;
        if (Duration > TimeSpan.Zero)
            target = Math.Clamp(value: target, min: 0, max: Duration.TotalSeconds);

        SetPosition(Math.Max(val1: 0, val2: target));
        Restart(_mediaTime);
    }

    /// <summary>Seek by a delta — the ±10 s keys, without the clamping arithmetic at the call site.</summary>
    public void SeekBy(TimeSpan delta) => Seek(_position.Value + delta);

    /// <summary>
    ///     One frame of the host's loop: advance the clock, present whatever frame is due, notice the
    ///     end. Must run on the thread that owns the GPU, since this is where the texture is uploaded.
    ///     Cheap and safe to call when nothing is playing.
    /// </summary>
    public void Tick()
    {
        if (_disposed) return;

        var pipe = _pipe;
        if (pipe is null)
        {
            WarmUp();
            return;
        }

        double output = OutputTime(pipe);
        long target = (long)Math.Floor((output * pipe.Fps) + PresentSlackFrames);
        bool presented = PresentDue(pipe: pipe, targetIndex: target);

        if (_wantPlay && _state.Value != PlaybackState.Playing && Primed(
                pipe: pipe,
                presentedThisTick: presented
            ))
        {
            ResumeClock();
            _state.Value = PlaybackState.Playing;
        }

        SetPosition(pipe.StartSeconds + (output * Rate()));
        _buffered.Value = TimeSpan.FromSeconds(ReadAhead(pipe: pipe, output: output));

        if (_wantPlay && Finished(pipe)) HandleEnd(pipe);
        else if (_wantPlay && _state.Value == PlaybackState.Playing && Starved(pipe))
        {
            PauseClock();
            _state.Value = PlaybackState.Buffering;
        }
    }

    /// <summary>
    ///     Decode the opening frame of a freshly opened source so the view shows the video rather
    ///     than a black rectangle — and so the first <see cref="Play" /> starts instantly instead of
    ///     buffering from cold. Deliberately here and not in <see cref="OpenAsync" />: this runs on
    ///     the frame loop, where starting decoders and opening a mixer stream belong.
    /// </summary>
    private void WarmUp()
    {
        if (_state.Value != PlaybackState.Ready || TextureHandle != 0) return;
        if (_media.Value is not { HasVideo: true }) return;

        Restart(_mediaTime);
    }

    // ── pipeline ────────────────────────────────────────────────────────────────

    private void Restart(double atSeconds)
    {
        StopPipeline();
        if (_media.Value is null || _disposed) return;

        var media = _media.Value;
        double fps = FFmpeg.SaneFrameRate(media.Video?.FrameRate ?? 25.0);
        var pipe = new Pipeline(
            startSeconds: atSeconds,
            fps: fps,
            capacity: FrameQueueCapacity(media: media, fps: fps)
        );

        try
        {
            if (media.HasVideo) StartVideo(pipe);
            if (media.HasAudio) StartAudio(pipe);
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            Fail(ex.Message);
            return;
        }

        _pipe = pipe;
        _held = null;
        _useWallClock = !media.HasAudio;
        _wall.Reset();
        if (_wantPlay) _state.Value = PlaybackState.Buffering;
    }

    private void StartVideo(Pipeline pipe)
    {
        var media = _media.Value!;

        // Geometry has to be known before the first read: rawvideo carries no header, so the frame
        // size is the only thing that says where one frame ends and the next begins. This mirrors
        // what the scale filter in the arguments will do.
        (int w, int h) = ScaledSize(track: media.Video!, maxHeight: MaxHeight);
        pipe.Width = w;
        pipe.Height = h;

        pipe.Video = Spawn(
            args: FFmpeg.VideoArgs(
                source: media.Source,
                startSeconds: pipe.StartSeconds,
                fps: pipe.Fps,
                speed: Rate(),
                maxHeight: MaxHeight
            ),
            pipe: pipe
        );

        var stdout = pipe.Video.StandardOutput.BaseStream;
        pipe.VideoThread = StartThread(
            name: "zigote-video-decode",
            body: () => ReadVideo(pipe: pipe, stdout: stdout)
        );
    }

    private void StartAudio(Pipeline pipe)
    {
        if (_audio is null) return; // silent by construction: no device, or the caller wants none

        pipe.AudioStreamId = _audio.CreateStream();
        if (pipe.AudioStreamId == 0) return;

        _audio.SetSpatial(id: pipe.AudioStreamId, enabled: false);
        _audio.SetVolume(id: pipe.AudioStreamId, volume: Gain());

        pipe.Audio = Spawn(
            args: FFmpeg.AudioArgs(
                source: _media.Value!.Source,
                startSeconds: pipe.StartSeconds,
                speed: Rate()
            ),
            pipe: pipe
        );

        var stdout = pipe.Audio.StandardOutput.BaseStream;
        pipe.AudioThread = StartThread(
            name: "zigote-audio-decode",
            body: () => ReadAudio(pipe: pipe, stdout: stdout, audio: _audio)
        );
    }

    /// <summary>
    ///     How many decoded frames to hold: <see cref="TargetBufferSeconds" /> worth, capped by
    ///     <see cref="MaxBufferBytes" />. The cap is what does the work at high resolutions — two
    ///     seconds of 1080p is 500 MB, so 64 MB buys eight frames there and a comfortable second at
    ///     540p. Audio carries the rest of the read-ahead; its ring is seconds deep for the same
    ///     memory a couple of frames cost.
    /// </summary>
    internal int FrameQueueCapacity(MediaInfo media, double fps)
    {
        if (media.Video is not { } track) return MinFrameQueueDepth;

        (int w, int h) = ScaledSize(track: track, maxHeight: MaxHeight);
        long frameBytes = (long)Math.Max(val1: 1, val2: w) * Math.Max(val1: 1, val2: h) * 4;
        int byBytes = (int)Math.Min(
            val1: int.MaxValue,
            val2: Math.Max(val1: 1, val2: MaxBufferBytes / frameBytes)
        );
        int bySeconds = (int)Math.Ceiling(TargetBufferSeconds * fps);
        return Math.Max(val1: MinFrameQueueDepth, val2: Math.Min(val1: byBytes, val2: bySeconds));
    }

    /// <summary>
    ///     What the video pipe will actually emit. Mirrors <c>scale=-2:min(h,ih)</c>: never upscale,
    ///     round the width to an even number.
    /// </summary>
    internal static (int Width, int Height) ScaledSize(VideoTrackInfo track, int maxHeight)
    {
        if (maxHeight <= 0 || track.Height <= maxHeight || track.Height <= 0)
            return (track.Width, track.Height);

        double scale = (double)maxHeight / track.Height;
        int width = (int)Math.Round(track.Width * scale / 2.0) * 2;
        return (Math.Max(val1: 2, val2: width), maxHeight);
    }

    private static Process Spawn(IEnumerable<string> args, Pipeline pipe)
    {
        var psi = new ProcessStartInfo(FFmpeg.FfmpegPath) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException(
                       $"Could not start '{FFmpeg.FfmpegPath}'."
                   );

        // Drained on its own thread: a full stderr pipe blocks ffmpeg mid-decode, and the tail of it
        // is the only explanation available when a source turns out to be unplayable.
        var stderr = proc.StandardError;
        StartThread(
            name: "zigote-ffmpeg-stderr",
            body: () =>
            {
                try
                {
                    string? line;
                    while ((line = stderr.ReadLine()) is not null) pipe.NoteError(line);
                }
                catch (Exception)
                {
                    // Killed mid-read during teardown.
                }
            }
        );
        return proc;
    }

    private static Thread StartThread(string name, Action body)
    {
        var t = new Thread(() => body()) {
            IsBackground = true, // never keeps the process alive past a forgotten Dispose
            Name = name,
        };
        t.Start();
        return t;
    }

    private static void ReadVideo(Pipeline pipe, Stream stdout)
    {
        int frameBytes = pipe.Width * pipe.Height * 4;
        if (frameBytes <= 0)
        {
            pipe.VideoEof = true;
            return;
        }

        long index = 0;
        try
        {
            while (!pipe.Cancelled)
            {
                byte[] buffer = pipe.RentFrame(frameBytes);
                try
                {
                    stdout.ReadExactly(buffer: buffer, offset: 0, count: frameBytes);
                }
                catch (Exception)
                {
                    // EOF, or the process was killed out from under the read.
                    pipe.ReturnFrame(buffer);
                    break;
                }

                // Blocks once the ring is full — that backpressure is what throttles ffmpeg, and
                // what keeps a fast network source from decoding the whole file into RAM.
                pipe.Frames.Add(new Frame(Buffer: buffer, Index: index));
                pipe.QueuedIndex = index++;
            }
        }
        catch (Exception)
        {
            // CompleteAdding() during teardown, or a disposed collection.
        }
        finally
        {
            pipe.VideoEof = true;
        }
    }

    private static void ReadAudio(Pipeline pipe, Stream stdout, IAudioApi audio)
    {
        // 16 KB ≈ 85 ms of 48 kHz stereo: small enough that a seek does not strand much decoded
        // audio, large enough that the push is not the hot path.
        byte[] buffer = new byte[16 * 1024];
        uint id = pipe.AudioStreamId;

        try
        {
            PushAll(
                pipe: pipe,
                audio: audio,
                id: id,
                bytes: FFmpeg.WavHeader()
            );

            while (!pipe.Cancelled)
            {
                int read = stdout.Read(buffer: buffer, offset: 0, count: buffer.Length);
                if (read <= 0) break;
                if (!PushAll(
                        pipe: pipe,
                        audio: audio,
                        id: id,
                        bytes: buffer.AsSpan(start: 0, length: read)
                    )) break;
            }
        }
        catch (Exception)
        {
            // Killed during teardown, or the device went away.
        }
        finally
        {
            pipe.AudioEof = true;
            if (!pipe.Cancelled) audio.StreamFinish(id);
        }
    }

    /// <summary>
    ///     Hand every byte over, waiting out a full queue. A short push means the engine's buffer is
    ///     full, which is the signal to stop reading the pipe rather than to grow a buffer here.
    /// </summary>
    private static bool PushAll(Pipeline pipe, IAudioApi audio, uint id, ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (pipe.Cancelled) return false;
            int taken = audio.StreamPush(id: id, bytes: bytes[offset..]);
            if (taken > 0)
            {
                offset += taken;
                continue;
            }

            Thread.Sleep(5);
        }

        return true;
    }

    private void StopPipeline()
    {
        var pipe = _pipe;
        _pipe = null;
        _held = null;
        _wall.Reset();
        _buffered.Value = TimeSpan.Zero;
        if (pipe is null) return;

        pipe.Dispose();

        // Safe after cancellation: the reader thread checks Cancelled before every push, and the
        // engine ignores calls against a destroyed id even if one slips through.
        if (pipe.AudioStreamId != 0) _audio?.Destroy(pipe.AudioStreamId);
    }

    // ── presentation ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Upload the newest frame that is due, discarding any older ones still queued. Dropping is
    ///     the correct response to being late: uploading three stale frames to catch up costs the
    ///     bandwidth that made us late.
    /// </summary>
    private bool PresentDue(Pipeline pipe, long targetIndex)
    {
        Frame? newest = null;

        while (true)
        {
            if (_held is null)
            {
                if (!pipe.Frames.TryTake(out var taken)) break;
                _held = taken;
            }

            if (_held.Value.Index > targetIndex) break;

            if (newest is not null) pipe.ReturnFrame(newest.Value.Buffer);
            newest = _held;
            _held = null;
        }

        if (newest is null) return false;

        Upload(rgba: newest.Value.Buffer, width: pipe.Width, height: pipe.Height);
        FramesPresented++;
        pipe.ReturnFrame(newest.Value.Buffer);
        return true;
    }

    private void Upload(byte[] rgba, int width, int height)
    {
        if (ZigoteEngine.Instance is null || width <= 0 || height <= 0) return;

        var pixels = rgba.AsSpan(start: 0, length: width * height * 4);

        // Steady state: the texture, its view and its bind group were built for the first frame and
        // every frame after it is a texel overwrite. Creating and releasing a 1080p texture sixty
        // times a second — which is what the load/release pair costs — churns both the GPU
        // allocator and the engine's image registry to draw the same rectangle.
        if (TextureHandle != 0
            && FrameSize == ((uint)width, (uint)height)
            && ZigoteEngine.UpdateTextureRgba(
                textureHandle: TextureHandle,
                rgba: pixels,
                width: (uint)width,
                height: (uint)height
            ))
            return;

        // First frame, or the geometry changed under us (a new source): an overwrite cannot resize,
        // so take a fresh handle and drop the old one.
        ulong handle = ZigoteEngine.LoadTextureFromRgba(
            rgba: pixels,
            width: (uint)width,
            height: (uint)height
        );
        if (handle == 0) return;

        if (TextureHandle != 0) ZigoteEngine.ReleaseTexture(TextureHandle);
        TextureHandle = handle;
        FrameSize = ((uint)width, (uint)height);
    }

    /// <summary>
    ///     Seconds of output the pipeline has actually played. The mixer's cursor is the master clock;
    ///     the wall clock stands in when there is no audio, and takes over permanently if the cursor
    ///     turns out not to move.
    /// </summary>
    private double OutputTime(Pipeline pipe)
    {
        if (_useWallClock || pipe.AudioStreamId == 0 || _audio is null)
            return _wall.Elapsed.TotalSeconds;

        float cursor = _audio.Cursor(pipe.AudioStreamId);
        if (cursor < 0)
        {
            _useWallClock = true;
            return _wall.Elapsed.TotalSeconds;
        }

        // A cursor pinned at zero while the wall clock runs means the device is not consuming the
        // stream. Time out rather than freeze the picture on frame one forever.
        if (cursor <= 0 && _wall.Elapsed.TotalSeconds > AudioClockStallSeconds)
        {
            _useWallClock = true;
            return _wall.Elapsed.TotalSeconds;
        }

        return cursor;
    }

    /// <summary>
    ///     Media seconds already decoded past the play head. Video read-ahead is the queued frames;
    ///     audio's is what the mixer has yet to play. The smaller wins, because playback stops on
    ///     whichever side empties first — and both are output seconds, so speed converts them to
    ///     media seconds.
    /// </summary>
    private double ReadAhead(Pipeline pipe, double output)
    {
        double ahead = double.PositiveInfinity;

        if (pipe.HasVideo)
        {
            double queuedOutput = (pipe.QueuedIndex + 1) / pipe.Fps;
            ahead = Math.Max(val1: 0, val2: queuedOutput - output);
        }

        if (pipe.AudioStreamId != 0 && _audio is not null && !pipe.AudioEof)
            ahead = Math.Min(val1: ahead, val2: _audio.StreamBuffered(pipe.AudioStreamId));

        return double.IsFinite(ahead) ? ahead * Rate() : 0;
    }

    /// <summary>Enough decoded on both sides to start the clock without stuttering immediately.</summary>
    private bool Primed(Pipeline pipe, bool presentedThisTick)
    {
        bool haveVideo = !pipe.HasVideo || presentedThisTick || TextureHandle != 0;
        if (!haveVideo) return false;

        if (pipe.AudioStreamId == 0 || _useWallClock || _audio is null) return true;

        return pipe.AudioEof || _audio.StreamBuffered(pipe.AudioStreamId) >= PrerollSeconds;
    }

    /// <summary>Playing, but the decoder has fallen behind and there is nothing left to present.</summary>
    private static bool Starved(Pipeline pipe) =>
        pipe.HasVideo && !pipe.VideoEof && pipe.Frames.Count == 0;

    private bool Finished(Pipeline pipe)
    {
        if (pipe.HasVideo)
            return pipe.VideoEof && pipe.Frames.Count == 0 && _held is null;

        // Audio-only: the mixer has to run out too, not just the pipe.
        if (!pipe.AudioEof) return false;
        return _audio is null || pipe.AudioStreamId == 0 || _audio.AtEnd(pipe.AudioStreamId);
    }

    private void HandleEnd(Pipeline pipe)
    {
        // A pipeline that ends without ever presenting a frame did not finish, it died — the source
        // moved, the URL 404'd, the codec was missing. ffmpeg's stderr is the only account of that,
        // so report it as a failure rather than as a zero-length video that played fine.
        if (pipe.HasVideo && TextureHandle == 0)
        {
            string errors = pipe.Errors();
            Fail(
                errors.Length > 0
                    ? FFmpeg.Tail(errors)
                    : $"ffmpeg produced no frames for '{_media.Value?.Source}'."
            );
            return;
        }

        if (Loop.Value)
        {
            SetPosition(0);
            Restart(0);
            return;
        }

        if (Duration > TimeSpan.Zero) SetPosition(Duration.TotalSeconds);
        _wantPlay = false;
        StopPipeline();
        _state.Value = PlaybackState.Ended;
    }

    // ── transport plumbing ──────────────────────────────────────────────────────

    /// <summary>Rate actually handed to ffmpeg — the signal is free-form, the pipeline is not.</summary>
    private double Rate() => Math.Clamp(value: Speed.Value, min: 0.25, max: 4.0);

    /// <summary>Gain actually handed to the mixer, mute folded in.</summary>
    private float Gain() => Muted.Value ? 0f : Math.Clamp(value: Volume.Value, min: 0f, max: 1f);

    private void SetPosition(double seconds)
    {
        _mediaTime = Math.Max(val1: 0, val2: seconds);
        _position.Value = TimeSpan.FromSeconds(_mediaTime);
    }

    private void ResumeClock()
    {
        _wall.Start();
        if (_pipe?.AudioStreamId is > 0) _audio?.Play(_pipe.AudioStreamId);
    }

    private void PauseClock()
    {
        _wall.Stop();
        // Stop() rewinds a seekable source; the push stream has nothing to rewind, so its
        // delivered-sample cursor simply stops advancing — which is exactly a paused clock.
        if (_pipe?.AudioStreamId is > 0) _audio?.Stop(_pipe.AudioStreamId);
    }

    private void ApplyVolume()
    {
        if (_pipe?.AudioStreamId is > 0)
            _audio?.SetVolume(id: _pipe.AudioStreamId, volume: Gain());
    }

    private void Fail(string message)
    {
        _wantPlay = false;
        StopPipeline();
        _error.Value = message;
        _state.Value = PlaybackState.Failed;
    }

    private readonly record struct Frame(byte[] Buffer, long Index);

    /// <summary>
    ///     One run of the decoders, from a given media offset at a given speed. Everything that a
    ///     seek or a speed change invalidates lives here, so restarting is constructing a new one
    ///     rather than resetting a dozen fields.
    /// </summary>
    private sealed class Pipeline(double startSeconds, double fps, int capacity) : IDisposable
    {
        public readonly double Fps = fps;
        public readonly BlockingCollection<Frame> Frames = new(capacity);
        public readonly double StartSeconds = startSeconds;
        private readonly CancellationTokenSource _cts = new();
        private readonly StringBuilder _errors = new();

        /// <summary>
        ///     Frame buffers this pipeline has already allocated, waiting to be filled again.
        ///     <para>
        ///         Not <see cref="ArrayPool{T}.Shared" />: it declines to pool anything over 1 MB, and
        ///         every frame here is bigger than that from 540p up — so the shared pool would hand
        ///         back a fresh multi-megabyte allocation per frame, which is the allocation this is
        ///         meant to avoid. Every buffer is exactly one frame, so a free list is the whole
        ///         data structure needed.
        ///     </para>
        /// </summary>
        private readonly ConcurrentBag<byte[]> _spare = [];

        public Process? Audio;
        public volatile bool AudioEof;
        public uint AudioStreamId;
        public Thread? AudioThread;
        public int Height;

        /// <summary>Index of the newest frame handed to the queue; -1 before the first one.</summary>
        public long QueuedIndex = -1;

        public Process? Video;
        public volatile bool VideoEof;
        public Thread? VideoThread;
        public int Width;

        public bool Cancelled => _cts.IsCancellationRequested;

        public bool HasVideo => Video is not null;

        public void Dispose()
        {
            _cts.Cancel();

            // Unblocks a reader parked on a full queue, so the kill below is the only thing a read
            // can still be waiting on.
            try
            {
                Frames.CompleteAdding();
            }
            catch (Exception)
            {
                // Already completed.
            }

            Kill(Video);
            Kill(Audio);

            // The readers are background threads that exit as soon as their pipe closes; not joining
            // keeps teardown off the frame budget. Queued frames were pool-rented — dropping them is
            // allowed, the pool simply allocates fresh ones.
            while (Frames.TryTake(out _)) { }
        }

        private static void Kill(Process? proc)
        {
            if (proc is null) return;
            try
            {
                if (!proc.HasExited) proc.Kill(true);
            }
            catch (Exception)
            {
                // Already gone.
            }
            finally
            {
                proc.Dispose();
            }
        }

        /// <summary>A buffer of exactly <paramref name="frameBytes" />, recycled where possible.</summary>
        public byte[] RentFrame(int frameBytes)
        {
            return _spare.TryTake(out byte[]? buffer) && buffer.Length == frameBytes
                ? buffer
                : new byte[frameBytes];
        }

        public void ReturnFrame(byte[] buffer)
        {
            // Bounded by construction: at most `capacity` frames exist at once, so the bag can never
            // grow past the ring it feeds.
            if (!Cancelled) _spare.Add(buffer);
        }

        public void NoteError(string line)
        {
            lock (_errors)
            {
                if (_errors.Length < 4096)
                    _errors.AppendLine(line);
            }
        }

        public string Errors()
        {
            lock (_errors) return _errors.ToString();
        }
    }
}
