using System.Diagnostics;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.Core.Engine;
using Zigote.Videoplayer;
using Xunit;

namespace Zigote.Tests;

/// <summary>
///     The parts of the video player that can be asserted without a GPU: the ffmpeg command lines,
///     the probe mapping, the letterbox arithmetic — plus one end-to-end run against a real clip,
///     skipped where ffmpeg is not installed.
/// </summary>
public class VideoPlayerTests
{
    /// <summary>A trimmed ffprobe dump: an h264/aac mp4, plus the cover art that must be ignored.</summary>
    private const string ProbeJson = """
    {
      "streams": [
        {
          "index": 0, "codec_name": "mjpeg", "codec_type": "video",
          "width": 600, "height": 600, "r_frame_rate": "90000/1",
          "disposition": { "attached_pic": 1 }
        },
        {
          "index": 1, "codec_name": "h264", "codec_type": "video",
          "width": 1920, "height": 1080,
          "r_frame_rate": "30000/1001", "avg_frame_rate": "30000/1001"
        },
        {
          "index": 2, "codec_name": "aac", "codec_type": "audio",
          "channels": 2, "sample_rate": "44100",
          "tags": { "language": "eng" }
        }
      ],
      "format": { "duration": "128.500000" }
    }
    """;

    [Fact]
    public void Probe_Maps_Streams_And_Skips_Cover_Art()
    {
        var info = FFmpeg.ParseProbe("/tmp/clip.mp4", ProbeJson);

        Assert.Equal(TimeSpan.FromSeconds(128.5), info.Duration);
        Assert.True(info.IsSeekable);

        // The mjpeg attached_pic comes first in the stream list; taking it would make a 1-frame
        // still the video track and hang the clock on it.
        Assert.Equal("h264", info.Video!.Codec);
        Assert.Equal(1920, info.Video.Width);
        Assert.Equal(29.97, info.Video.FrameRate, 2);

        Assert.Equal("aac", info.Audio!.Codec);
        Assert.Equal(44100, info.Audio.SampleRate);
        Assert.Equal("eng", info.Audio.Language);
    }

    [Fact]
    public void Probe_Rejects_A_Source_With_No_Streams()
    {
        Assert.Throws<InvalidOperationException>(
            () => FFmpeg.ParseProbe("x", """{ "streams": [], "format": {} }""")
        );
    }

    [Fact]
    public void Video_Args_Emit_Fixed_Rate_Rgba_From_The_Seek_Point()
    {
        var args = FFmpeg.VideoArgs("/tmp/a b.mp4", 12.5, 25, 1.0, 0).ToArray();

        // The path is one element, not a quoted fragment — a space in it is not a parsing question.
        Assert.Contains("/tmp/a b.mp4", args);
        Assert.Equal("12.5", args[Array.IndexOf(args, "-ss") + 1]);
        Assert.Equal("rawvideo", args[Array.IndexOf(args, "-f") + 1]);
        Assert.Equal("rgba", args[Array.IndexOf(args, "-pix_fmt") + 1]);
        Assert.Equal("cfr", args[Array.IndexOf(args, "-fps_mode") + 1]);
        Assert.Equal("25", args[Array.IndexOf(args, "-r") + 1]);

        // No filter at 1× and native size: the frames are already what we want.
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void Video_Args_Scale_And_Retime_Only_When_Asked()
    {
        var args = FFmpeg.VideoArgs("in.mkv", 0, 30, 2.0, 720).ToArray();
        var filters = args[Array.IndexOf(args, "-vf") + 1];

        Assert.Contains("setpts=PTS/2", filters);
        Assert.Contains("scale=-2:min(720\\,ih)", filters);
        Assert.DoesNotContain("-ss", args); // position zero needs no seek
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(4.0, 4.0)]
    [InlineData(0.25, 0.25)]
    public void ATempo_Stages_Multiply_Back_To_The_Requested_Speed(double speed, double expected)
    {
        var product = FFmpeg.ATempo(speed)
            .Split(',')
            .Select(stage => double.Parse(stage.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture))
            .Aggregate(1.0, (a, b) => a * b);

        // Chained stages are what keeps 4× from being a chipmunk; each one must stay in atempo's
        // supported range, and together they must land on the rate that was asked for.
        Assert.Equal(expected, product, 6);
        foreach (var stage in FFmpeg.ATempo(speed).Split(','))
        {
            var factor = double.Parse(stage.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(factor, 0.5, 2.0);
        }
    }

    [Fact]
    public void Wav_Header_Describes_The_Pcm_The_Audio_Pipe_Produces()
    {
        var h = FFmpeg.WavHeader();

        Assert.Equal(44, h.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(h, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(h, 8, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(h, 36, 4));
        Assert.Equal(1, BitConverter.ToUInt16(h, 20)); // PCM
        Assert.Equal(FFmpeg.AudioChannels, BitConverter.ToUInt16(h, 22));
        Assert.Equal((uint)FFmpeg.AudioSampleRate, BitConverter.ToUInt32(h, 24));
        Assert.Equal(16, BitConverter.ToUInt16(h, 34));

        // Declared length must stay block-aligned and positive as a signed value, or the decoder
        // reads a partial frame at the end of a stream whose real length is unknown.
        var declared = BitConverter.ToUInt32(h, 40);
        Assert.True(declared <= int.MaxValue);
        Assert.Equal(0u, declared % (FFmpeg.AudioChannels * 2));
    }

    [Fact]
    public void Scaled_Size_Never_Upscales_And_Keeps_Width_Even()
    {
        var uhd = new VideoTrackInfo(3840, 2160, 30, "hevc");
        Assert.Equal((1280, 720), VideoPlayer.ScaledSize(uhd, 720));

        // Already smaller than the cap: left alone rather than stretched up to it.
        var small = new VideoTrackInfo(640, 360, 30, "h264");
        Assert.Equal((640, 360), VideoPlayer.ScaledSize(small, 720));
        Assert.Equal((640, 360), VideoPlayer.ScaledSize(small, 0));

        // 1.85:1 at 400 px tall is 741.6 wide — the odd rounding chroma planes cannot take.
        var scope = new VideoTrackInfo(1850, 1000, 24, "prores");
        var (w, _) = VideoPlayer.ScaledSize(scope, 400);
        Assert.Equal(0, w % 2);
    }

    [Fact]
    public void Fit_Rect_Letterboxes_Contains_And_Crops_Covers()
    {
        var box = new Rect(0, 0, 400, 400);

        // 16:9 inside a square: full width, bars top and bottom, centred.
        var contain = VideoView.FitRect(box, 16f / 9f, VideoFit.Contain);
        Assert.Equal(400, contain.Width, 2);
        Assert.Equal(225, contain.Height, 2);
        Assert.Equal(87.5, contain.Y, 2);

        // Cover fills the square instead, overflowing horizontally by the same aspect.
        var cover = VideoView.FitRect(box, 16f / 9f, VideoFit.Cover);
        Assert.Equal(400, cover.Height, 2);
        Assert.True(cover.Width > 400);
        Assert.Equal((400 - cover.Width) / 2, cover.X, 2);

        Assert.Equal(box, VideoView.FitRect(box, 16f / 9f, VideoFit.Fill));
    }

    [Fact]
    public void Clock_Grows_A_Field_Only_Past_An_Hour()
    {
        Assert.Equal("0:00", VideoControls.Clock(0));
        Assert.Equal("0:07", VideoControls.Clock(7.4));
        Assert.Equal("2:05", VideoControls.Clock(125));
        Assert.Equal("1:00:00", VideoControls.Clock(3600));
    }

    /// <summary>
    ///     The whole pipeline against a clip ffmpeg renders on the spot: probe, spawn, decode, and a
    ///     clock that advances. No engine is running, so frames are presented but not uploaded —
    ///     which is exactly the part this can check without a GPU.
    /// </summary>
    [Fact]
    public async Task Plays_A_Real_Clip_And_Advances_The_Clock()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable(), "ffmpeg/ffprobe not installed");

        var path = Path.Combine(Path.GetTempPath(), $"zigote-vp-test-{Environment.ProcessId}.mp4");
        try
        {
            await RenderClip(path);

            using var player = new VideoPlayer();
            var info = await player.OpenAsync(path, ct: TestContext.Current.CancellationToken);

            Assert.Equal(PlaybackState.Ready, player.State.Value);
            Assert.Equal(320, info.Video!.Width);
            Assert.Equal(3, info.Duration.TotalSeconds, 1);

            player.Play();

            // Nothing pumps the frame loop in a test process, so drive the tickers directly and
            // give the child process real time to decode into the queue.
            var deadline = Stopwatch.StartNew();
            while (player.Position.Value < TimeSpan.FromSeconds(0.5)
                   && deadline.Elapsed.TotalSeconds < 20)
            {
                player.Tick();
                await Task.Delay(16, TestContext.Current.CancellationToken);
            }

            Assert.True(
                player.Position.Value >= TimeSpan.FromSeconds(0.5),
                $"clock stalled at {player.Position.Value} in state {player.State.Value} "
                + $"({player.Error.Value})"
            );
            Assert.NotEqual(PlaybackState.Failed, player.State.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Reports_A_Missing_Source_Instead_Of_Hanging()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable(), "ffmpeg/ffprobe not installed");

        using var player = new VideoPlayer();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => player.OpenAsync(
                "/nonexistent/does-not-exist.mp4",
                ct: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(PlaybackState.Failed, player.State.Value);
        Assert.False(string.IsNullOrWhiteSpace(player.Error.Value));
    }


    /// <summary>
    ///     Streaming for real: probe an https source, decode it, and watch both the clock and the
    ///     read-ahead move. Skipped when the network (or the host) is not there — it is the only
    ///     test here that depends on something outside the machine.
    /// </summary>
    [Fact]
    public async Task Plays_An_Https_Source_And_Reports_Read_Ahead()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable(), "ffmpeg/ffprobe not installed");

        const string url =
            "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4";

        MediaInfo info;
        using var player = new VideoPlayer(new RecordingAudioApi { Buffered = 1f });
        try
        {
            info = await player.OpenAsync(url, 360, TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            Assert.Skip($"network unavailable: {ex.Message}");
            return;
        }

        Assert.True(info.IsNetwork);
        Assert.False(info.IsLive); // a progressive mp4 has a length, so it stays seekable
        Assert.True(info.IsSeekable);

        player.Play();
        var deadline = Stopwatch.StartNew();
        var sawReadAhead = false;
        while (deadline.Elapsed.TotalSeconds < 30
               && (player.Position.Value < TimeSpan.FromSeconds(0.5) || !sawReadAhead))
        {
            player.Tick();
            if (player.Buffered.Value > TimeSpan.Zero) sawReadAhead = true;
            await Task.Delay(16, TestContext.Current.CancellationToken);
        }

        Assert.NotEqual(PlaybackState.Failed, player.State.Value);
        Assert.True(
            player.Position.Value >= TimeSpan.FromSeconds(0.5),
            $"stalled at {player.Position.Value} in {player.State.Value} ({player.Error.Value})"
        );
        Assert.True(sawReadAhead, "the buffered-ahead signal never left zero");

        // 360p cap on this 720p source: that is the geometry the reader slices the raw pipe at, and
        // it must be derived from the stream the probe measured. (FrameSize stays 0 here — nothing
        // uploads without an engine — so the cap is checked at its source.)
        Assert.Equal((640, 360), VideoPlayer.ScaledSize(info.Video!, 360));
    }

    [Fact]
    public void Network_Sources_Get_Reconnect_Options_And_Local_Ones_Do_Not()
    {
        var net = FFmpeg.VideoArgs("https://cdn.example/clip.mp4", 0, 30, 1.0, 0).ToArray();
        var local = FFmpeg.VideoArgs("/media/clip.mp4", 0, 30, 1.0, 0).ToArray();

        // Without these a momentary 5xx from a CDN edge ends the read, and the player reports a
        // finished video rather than a hiccup.
        Assert.Equal("1", net[Array.IndexOf(net, "-reconnect") + 1]);
        Assert.Contains("-reconnect_streamed", net);
        Assert.Contains("-reconnect_on_network_error", net);
        Assert.Equal("8", net[Array.IndexOf(net, "-reconnect_delay_max") + 1]);

        // They are http-protocol options: ffmpeg rejects them outright on a file input, so a local
        // path must not carry them.
        Assert.DoesNotContain("-reconnect", local);
        Assert.DoesNotContain("-multiple_requests", local);

        // Ordering matters as much as presence — a protocol option after -i applies to the output.
        Assert.True(Array.IndexOf(net, "-reconnect") < Array.IndexOf(net, "-i"));
    }

    [Theory]
    [InlineData("https://host/a.m3u8", true)]
    [InlineData("http://host/a.mp4", true)]
    [InlineData("rtsp://host/live", true)]
    [InlineData("/media/clip.mp4", false)]
    [InlineData("C:\\media\\clip.mp4", false)]
    public void Network_Sources_Are_Recognised(string source, bool expected)
    {
        Assert.Equal(expected, FFmpeg.IsNetwork(source));
    }

    [Fact]
    public void Both_Pipes_Pin_The_Same_Stream_As_The_Probe()
    {
        // A multi-variant HLS master exposes every rendition as a stream and ffmpeg's default pick
        // is the largest — a different size than ParseProbe measured off the first one, which would
        // make the reader slice the raw pipe at the wrong frame boundary.
        var video = FFmpeg.VideoArgs("https://host/master.m3u8", 0, 30, 1.0, 0).ToArray();
        Assert.Equal("0:v:0", video[Array.IndexOf(video, "-map") + 1]);

        // Optional, so a video-only source produces silence instead of failing the pipe.
        var audio = FFmpeg.AudioArgs("https://host/master.m3u8", 0, 1.0).ToArray();
        Assert.Equal("0:a:0?", audio[Array.IndexOf(audio, "-map") + 1]);
    }

    [Fact]
    public void An_Unmeasurable_Network_Source_Is_Live_And_Unseekable()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "width": 1280, "height": 720,
                         "r_frame_rate": "30/1" } ], "format": {} }
        """;

        var live = FFmpeg.ParseProbe("https://host/live.m3u8", json);
        Assert.True(live.IsLive);
        Assert.True(live.IsNetwork);
        Assert.False(live.IsSeekable); // no end to scrub to; the bar must not invent one

        // The same unmeasurable stream off a disk is a broken file, not a live feed.
        var file = FFmpeg.ParseProbe("/media/clip.mp4", json);
        Assert.False(file.IsLive);
        Assert.False(file.IsNetwork);
    }

    [Fact]
    public void The_Frame_Ring_Is_Capped_In_Bytes_Not_Just_Seconds()
    {
        var player = new VideoPlayer { MaxBufferBytes = 64L * 1024 * 1024 };
        var hd = new MediaInfo("x", TimeSpan.FromMinutes(1), new VideoTrackInfo(1920, 1080, 30, "h264"), null);

        // Two seconds of decoded 1080p RGBA is half a gigabyte; the byte cap is what stops the ring
        // from being sized in seconds at high resolutions.
        var frames = player.FrameQueueCapacity(hd, 30);
        Assert.InRange(frames, 3, 12);
        Assert.True(frames * 1920L * 1080 * 4 <= player.MaxBufferBytes);

        // Small frames hit the seconds target instead, and never fall below the floor.
        var small = new MediaInfo("x", TimeSpan.FromMinutes(1), new VideoTrackInfo(320, 180, 30, "h264"), null);
        Assert.Equal(60, player.FrameQueueCapacity(small, 30));

        player.MaxBufferBytes = 1;
        Assert.Equal(3, player.FrameQueueCapacity(hd, 30));
    }

    /// <summary>
    ///     The settable signals are the transport, not a mirror of it: writing <c>Muted</c> reaches
    ///     the mixer, and each read-only signal notifies on its own so a bound control can subscribe
    ///     to just the one it draws.
    /// </summary>
    [Fact]
    public async Task Signals_Drive_The_Mixer_And_Notify_Individually()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable(), "ffmpeg/ffprobe not installed");

        var path = Path.Combine(Path.GetTempPath(), $"zigote-vp-sig-{Environment.ProcessId}.mp4");
        try
        {
            await RenderClip(path);

            var mixer = new RecordingAudioApi { Buffered = 1f };
            using var player = new VideoPlayer(mixer);

            int states = 0, positions = 0;
            using var stateSub = player.State.Observe(() => states++);
            using var positionSub = player.Position.Observe(() => positions++);

            await player.OpenAsync(path, ct: TestContext.Current.CancellationToken);
            Assert.True(states > 0, "opening did not report a state change");

            player.Play();
            var deadline = Stopwatch.StartNew();
            while (positions == 0 && deadline.Elapsed.TotalSeconds < 20)
            {
                player.Tick();
                await Task.Delay(16, TestContext.Current.CancellationToken);
            }

            // Position moved without State moving with it — that separation is what lets a bar
            // redraw one label per frame instead of its whole subtree.
            Assert.True(positions > 0, "the position signal never fired");

            player.Muted.Value = true;
            Assert.Equal(0f, mixer.LastVolume);
            player.Muted.Value = false;
            player.Volume.Value = 0.25f;
            Assert.Equal(0.25f, mixer.LastVolume, 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    ///     The audio half, against a recording mixer: the push stream is opened, fed a WAV header
    ///     before any PCM — the engine's push source detects the container itself, so headerless
    ///     samples would be undecodable — and the transport is driven with it.
    /// </summary>
    [Fact]
    public async Task Feeds_The_Mixer_A_Wav_Header_Before_Any_Samples()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable(), "ffmpeg/ffprobe not installed");

        var path = Path.Combine(Path.GetTempPath(), $"zigote-vp-audio-{Environment.ProcessId}.mp4");
        try
        {
            await RenderClip(path);

            var mixer = new RecordingAudioApi { Buffered = 1f };
            using (var player = new VideoPlayer(mixer))
            {
                await player.OpenAsync(path, ct: TestContext.Current.CancellationToken);
                player.Play();

                // Audio buffers well before the first frame is presented, and the stream is only
                // started once both sides are primed — so wait for the start, not just the bytes.
                var deadline = Stopwatch.StartNew();
                while ((mixer.PushedBytes.Count < 4096 || !mixer.Played)
                       && deadline.Elapsed.TotalSeconds < 20)
                {
                    player.Tick();
                    await Task.Delay(16, TestContext.Current.CancellationToken);
                }

                Assert.NotEqual(0u, mixer.Created);
                Assert.True(mixer.PushedBytes.Count > 44, "no PCM reached the mixer");

                var head = mixer.PushedBytes.Take(12).ToArray();
                Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(head, 0, 4));
                Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(head, 8, 4));
                Assert.True(mixer.Played, "the stream was never started");

                player.Pause();
                Assert.True(mixer.Stopped, "pausing did not stop the stream");
            }

            // Disposing the player must hand the stream back, or a page that opens ten videos
            // leaks ten decoders into the mixer.
            Assert.Contains(mixer.Created, mixer.Destroyed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task RenderClip(string path)
    {
        var psi = new ProcessStartInfo(FFmpeg.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                     "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=3",
                     "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                     "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                     "-c:a", "aac", "-shortest", path,
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stderr = await proc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await proc.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(proc.ExitCode == 0, stderr);
    }

    /// <summary>
    ///     An <see cref="IAudioApi" /> that records instead of playing: enough to assert what the
    ///     player hands the mixer, without a device. Only the stream half is real — the player never
    ///     touches files, equalizers or the spatial surface.
    /// </summary>
    private sealed class RecordingAudioApi : IAudioApi
    {
        private readonly Lock _gate = new();

        public List<byte> PushedBytes { get; } = [];
        public List<uint> Destroyed { get; } = [];
        public uint Created { get; private set; }
        public bool Finished { get; private set; }
        public bool Played { get; private set; }
        public bool Stopped { get; private set; }
        public float Buffered { get; set; }
        public float LastVolume { get; private set; } = -1f;
        public float CursorSeconds { get; set; } = -1f;

        public int OutputRate => 48000;

        public uint CreateStream()
        {
            return Created = 1;
        }

        public int StreamPush(uint id, ReadOnlySpan<byte> bytes)
        {
            // The reader runs on its own thread; the assertions run on the test's.
            lock (_gate) PushedBytes.AddRange(bytes);
            return bytes.Length;
        }

        public void StreamFinish(uint id)
        {
            Finished = true;
        }

        public AudioStreamState StreamStatus(uint id)
        {
            return AudioStreamState.Playing;
        }

        public float StreamBuffered(uint id)
        {
            return Buffered;
        }

        public void Play(uint id)
        {
            Played = true;
        }

        public void Stop(uint id)
        {
            Stopped = true;
        }

        public void Destroy(uint id)
        {
            Destroyed.Add(id);
        }

        public float Cursor(uint id)
        {
            return CursorSeconds;
        }

        public bool AtEnd(uint id)
        {
            return Finished;
        }

        public uint CreateFile(string path, bool streaming) => 0;
        public void Seek(uint id, float seconds) { }
        public void ScheduleStart(uint id, float secondsFromNow) { }
        public float Duration(uint id) => -1f;
        public bool IsPlaying(uint id) => Played && !Stopped;
        public void SetVolume(uint id, float volume)
        {
            LastVolume = volume;
        }

        public void SetRate(uint id, float rate) { }
        public void SetSpatial(uint id, bool enabled) { }
        public void SetEq(uint id, uint eqId) { }
        public uint EqCreate(int bandCount) => 0;

        public void EqSetBand(uint eqId, int index, AudioBandKind kind, float freqHz, float gainDb,
            float q) { }

        public void EqSetEnabled(uint eqId, bool enabled) { }
        public void EqDestroy(uint eqId) { }
        public int Reopen(int sampleRateHz) => 48000;

        public float[] DecodeFile(string path, out int channels, out int sampleRate)
        {
            channels = 2;
            sampleRate = 48000;
            return [];
        }
    }
}
