using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.State;
using Zigote.Videoplayer;

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
        var info = FFmpeg.ParseProbe(source: "/tmp/clip.mp4", json: ProbeJson);

        Assert.Equal(expected: TimeSpan.FromSeconds(128.5), actual: info.Duration);
        Assert.True(info.IsSeekable);

        // The mjpeg attached_pic comes first in the stream list; taking it would make a 1-frame
        // still the video track and hang the clock on it.
        Assert.Equal(expected: "h264", actual: info.Video!.Codec);
        Assert.Equal(expected: 1920, actual: info.Video.Width);
        Assert.Equal(expected: 29.97, actual: info.Video.FrameRate, precision: 2);

        Assert.Equal(expected: "aac", actual: info.Audio!.Codec);
        Assert.Equal(expected: 44100, actual: info.Audio.SampleRate);
        Assert.Equal(expected: "eng", actual: info.Audio.Language);
    }

    [Fact]
    public void Probe_Rejects_A_Source_With_No_Streams()
    {
        Assert.Throws<InvalidOperationException>(() => FFmpeg.ParseProbe(
                source: "x",
                json: """{ "streams": [], "format": {} }"""
            )
        );
    }

    [Fact]
    public void Video_Args_Emit_Fixed_Rate_Rgba_From_The_Seek_Point()
    {
        string[] args = FFmpeg.VideoArgs(
            source: "/tmp/a b.mp4",
            startSeconds: 12.5,
            fps: 25,
            speed: 1.0,
            maxHeight: 0
        ).ToArray();

        // The path is one element, not a quoted fragment — a space in it is not a parsing question.
        Assert.Contains(expected: "/tmp/a b.mp4", collection: args);
        Assert.Equal(expected: "12.5", actual: args[Array.IndexOf(array: args, value: "-ss") + 1]);
        Assert.Equal(
            expected: "rawvideo",
            actual: args[Array.IndexOf(array: args, value: "-f") + 1]
        );
        Assert.Equal(
            expected: "rgba",
            actual: args[Array.IndexOf(array: args, value: "-pix_fmt") + 1]
        );
        Assert.Equal(
            expected: "cfr",
            actual: args[Array.IndexOf(array: args, value: "-fps_mode") + 1]
        );
        Assert.Equal(expected: "25", actual: args[Array.IndexOf(array: args, value: "-r") + 1]);

        // No filter at 1× and native size: the frames are already what we want.
        Assert.DoesNotContain(expected: "-vf", collection: args);
    }

    [Fact]
    public void Video_Args_Scale_And_Retime_Only_When_Asked()
    {
        string[] args = FFmpeg.VideoArgs(
            source: "in.mkv",
            startSeconds: 0,
            fps: 30,
            speed: 2.0,
            maxHeight: 720
        ).ToArray();
        string filters = args[Array.IndexOf(array: args, value: "-vf") + 1];

        Assert.Contains(expectedSubstring: "setpts=PTS/2", actualString: filters);
        Assert.Contains(expectedSubstring: "scale=-2:min(720\\,ih)", actualString: filters);
        Assert.DoesNotContain(expected: "-ss", collection: args); // position zero needs no seek
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(4.0, 4.0)]
    [InlineData(0.25, 0.25)]
    public void ATempo_Stages_Multiply_Back_To_The_Requested_Speed(double speed, double expected)
    {
        double product = FFmpeg.ATempo(speed)
            .Split(',')
            .Select(stage => double.Parse(
                    s: stage.Split('=')[1],
                    provider: CultureInfo.InvariantCulture
                )
            )
            .Aggregate(seed: 1.0, func: (a, b) => a * b);

        // Chained stages are what keeps 4× from being a chipmunk; each one must stay in atempo's
        // supported range, and together they must land on the rate that was asked for.
        Assert.Equal(expected: expected, actual: product, precision: 6);
        foreach (string stage in FFmpeg.ATempo(speed).Split(','))
        {
            double factor = double.Parse(
                s: stage.Split('=')[1],
                provider: CultureInfo.InvariantCulture
            );
            Assert.InRange(actual: factor, low: 0.5, high: 2.0);
        }
    }

    [Fact]
    public void Wav_Header_Describes_The_Pcm_The_Audio_Pipe_Produces()
    {
        byte[] h = FFmpeg.WavHeader();

        Assert.Equal(expected: 44, actual: h.Length);
        Assert.Equal(
            expected: "RIFF",
            actual: Encoding.ASCII.GetString(bytes: h, index: 0, count: 4)
        );
        Assert.Equal(
            expected: "WAVE",
            actual: Encoding.ASCII.GetString(bytes: h, index: 8, count: 4)
        );
        Assert.Equal(
            expected: "data",
            actual: Encoding.ASCII.GetString(bytes: h, index: 36, count: 4)
        );
        Assert.Equal(expected: 1, actual: BitConverter.ToUInt16(value: h, startIndex: 20)); // PCM
        Assert.Equal(
            expected: FFmpeg.AudioChannels,
            actual: BitConverter.ToUInt16(value: h, startIndex: 22)
        );
        Assert.Equal(
            expected: (uint)FFmpeg.AudioSampleRate,
            actual: BitConverter.ToUInt32(value: h, startIndex: 24)
        );
        Assert.Equal(expected: 16, actual: BitConverter.ToUInt16(value: h, startIndex: 34));

        // Declared length must stay block-aligned and positive as a signed value, or the decoder
        // reads a partial frame at the end of a stream whose real length is unknown.
        uint declared = BitConverter.ToUInt32(value: h, startIndex: 40);
        Assert.True(declared <= int.MaxValue);
        Assert.Equal(expected: 0u, actual: declared % (FFmpeg.AudioChannels * 2));
    }

    [Fact]
    public void Scaled_Size_Never_Upscales_And_Keeps_Width_Even()
    {
        var uhd = new VideoTrackInfo(
            Width: 3840,
            Height: 2160,
            FrameRate: 30,
            Codec: "hevc"
        );
        Assert.Equal(
            expected: (1280, 720),
            actual: VideoPlayer.ScaledSize(track: uhd, maxHeight: 720)
        );

        // Already smaller than the cap: left alone rather than stretched up to it.
        var small = new VideoTrackInfo(
            Width: 640,
            Height: 360,
            FrameRate: 30,
            Codec: "h264"
        );
        Assert.Equal(
            expected: (640, 360),
            actual: VideoPlayer.ScaledSize(track: small, maxHeight: 720)
        );
        Assert.Equal(
            expected: (640, 360),
            actual: VideoPlayer.ScaledSize(track: small, maxHeight: 0)
        );

        // 1.85:1 at 400 px tall is 741.6 wide — the odd rounding chroma planes cannot take.
        var scope = new VideoTrackInfo(
            Width: 1850,
            Height: 1000,
            FrameRate: 24,
            Codec: "prores"
        );
        (int w, _) = VideoPlayer.ScaledSize(track: scope, maxHeight: 400);
        Assert.Equal(expected: 0, actual: w % 2);
    }

    [Fact]
    public void Fit_Rect_Letterboxes_Contains_And_Crops_Covers()
    {
        var box = new Rect(
            x: 0,
            y: 0,
            width: 400,
            height: 400
        );

        // 16:9 inside a square: full width, bars top and bottom, centred.
        var contain = VideoView.FitRect(box: box, aspect: 16f / 9f, fit: VideoFit.Contain);
        Assert.Equal(expected: 400, actual: contain.Width, precision: 2);
        Assert.Equal(expected: 225, actual: contain.Height, precision: 2);
        Assert.Equal(expected: 87.5, actual: contain.Y, precision: 2);

        // Cover fills the square instead, overflowing horizontally by the same aspect.
        var cover = VideoView.FitRect(box: box, aspect: 16f / 9f, fit: VideoFit.Cover);
        Assert.Equal(expected: 400, actual: cover.Height, precision: 2);
        Assert.True(cover.Width > 400);
        Assert.Equal(expected: (400 - cover.Width) / 2, actual: cover.X, precision: 2);

        Assert.Equal(
            expected: box,
            actual: VideoView.FitRect(box: box, aspect: 16f / 9f, fit: VideoFit.Fill)
        );
    }

    [Fact]
    public void Clock_Grows_A_Field_Only_Past_An_Hour()
    {
        Assert.Equal(expected: "0:00", actual: VideoControls.Clock(0));
        Assert.Equal(expected: "0:07", actual: VideoControls.Clock(7.4));
        Assert.Equal(expected: "2:05", actual: VideoControls.Clock(125));
        Assert.Equal(expected: "1:00:00", actual: VideoControls.Clock(3600));
    }

    /// <summary>
    ///     The whole pipeline against a clip ffmpeg renders on the spot: probe, spawn, decode, and a
    ///     clock that advances. No engine is running, so frames are presented but not uploaded —
    ///     which is exactly the part this can check without a GPU.
    /// </summary>
    [Fact]
    public async Task Plays_A_Real_Clip_And_Advances_The_Clock()
    {
        Assert.SkipUnless(condition: FFmpeg.IsAvailable(), reason: "ffmpeg/ffprobe not installed");

        string path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote-vp-test-{Environment.ProcessId}.mp4"
        );
        try
        {
            await RenderClip(path);

            using var player = new VideoPlayer();
            var info = await player.OpenAsync(
                source: path,
                ct: TestContext.Current.CancellationToken
            );

            Assert.Equal(expected: PlaybackState.Ready, actual: player.State.Value);
            Assert.Equal(expected: 320, actual: info.Video!.Width);
            Assert.Equal(expected: 3, actual: info.Duration.TotalSeconds, precision: 1);

            player.Play();

            // Nothing pumps the frame loop in a test process, so drive the tickers directly and
            // give the child process real time to decode into the queue.
            var deadline = Stopwatch.StartNew();
            while (player.Position.Value < TimeSpan.FromSeconds(0.5)
                   && deadline.Elapsed.TotalSeconds < 20)
            {
                player.Tick();
                await Task.Delay(
                    millisecondsDelay: 16,
                    cancellationToken: TestContext.Current.CancellationToken
                );
            }

            Assert.True(
                condition: player.Position.Value >= TimeSpan.FromSeconds(0.5),
                userMessage:
                $"clock stalled at {player.Position.Value} in state {player.State.Value} "
                + $"({player.Error.Value})"
            );
            Assert.NotEqual(expected: PlaybackState.Failed, actual: player.State.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Reports_A_Missing_Source_Instead_Of_Hanging()
    {
        Assert.SkipUnless(condition: FFmpeg.IsAvailable(), reason: "ffmpeg/ffprobe not installed");

        using var player = new VideoPlayer();
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.OpenAsync(
                source: "/nonexistent/does-not-exist.mp4",
                ct: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(expected: PlaybackState.Failed, actual: player.State.Value);
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
        Assert.SkipUnless(condition: FFmpeg.IsAvailable(), reason: "ffmpeg/ffprobe not installed");

        const string url =
            "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4";

        MediaInfo info;
        using var player = new VideoPlayer(new RecordingAudioApi { Buffered = 1f });
        try
        {
            info = await player.OpenAsync(
                source: url,
                maxHeight: 360,
                ct: TestContext.Current.CancellationToken
            );
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
        bool sawReadAhead = false;
        while (deadline.Elapsed.TotalSeconds < 30
               && (player.Position.Value < TimeSpan.FromSeconds(0.5) || !sawReadAhead))
        {
            player.Tick();
            if (player.Buffered.Value > TimeSpan.Zero) sawReadAhead = true;
            await Task.Delay(
                millisecondsDelay: 16,
                cancellationToken: TestContext.Current.CancellationToken
            );
        }

        Assert.NotEqual(expected: PlaybackState.Failed, actual: player.State.Value);
        Assert.True(
            condition: player.Position.Value >= TimeSpan.FromSeconds(0.5),
            userMessage:
            $"stalled at {player.Position.Value} in {player.State.Value} ({player.Error.Value})"
        );
        Assert.True(
            condition: sawReadAhead,
            userMessage: "the buffered-ahead signal never left zero"
        );

        // 360p cap on this 720p source: that is the geometry the reader slices the raw pipe at, and
        // it must be derived from the stream the probe measured. (FrameSize stays 0 here — nothing
        // uploads without an engine — so the cap is checked at its source.)
        Assert.Equal(
            expected: (640, 360),
            actual: VideoPlayer.ScaledSize(track: info.Video!, maxHeight: 360)
        );
    }

    [Fact]
    public void Network_Sources_Get_Reconnect_Options_And_Local_Ones_Do_Not()
    {
        string[] net = FFmpeg.VideoArgs(
            source: "https://cdn.example/clip.mp4",
            startSeconds: 0,
            fps: 30,
            speed: 1.0,
            maxHeight: 0
        ).ToArray();
        string[] local = FFmpeg.VideoArgs(
            source: "/media/clip.mp4",
            startSeconds: 0,
            fps: 30,
            speed: 1.0,
            maxHeight: 0
        ).ToArray();

        // Without these a momentary 5xx from a CDN edge ends the read, and the player reports a
        // finished video rather than a hiccup.
        Assert.Equal(
            expected: "1",
            actual: net[Array.IndexOf(array: net, value: "-reconnect") + 1]
        );
        Assert.Contains(expected: "-reconnect_streamed", collection: net);
        Assert.Contains(expected: "-reconnect_on_network_error", collection: net);
        Assert.Equal(
            expected: "8",
            actual: net[Array.IndexOf(array: net, value: "-reconnect_delay_max") + 1]
        );

        // They are http-protocol options: ffmpeg rejects them outright on a file input, so a local
        // path must not carry them.
        Assert.DoesNotContain(expected: "-reconnect", collection: local);
        Assert.DoesNotContain(expected: "-multiple_requests", collection: local);

        // Ordering matters as much as presence — a protocol option after -i applies to the output.
        Assert.True(
            Array.IndexOf(array: net, value: "-reconnect") < Array.IndexOf(array: net, value: "-i")
        );
    }

    [Theory]
    [InlineData("https://host/a.m3u8", true)]
    [InlineData("http://host/a.mp4", true)]
    [InlineData("rtsp://host/live", true)]
    [InlineData("/media/clip.mp4", false)]
    [InlineData("C:\\media\\clip.mp4", false)]
    public void Network_Sources_Are_Recognised(string source, bool expected) => Assert.Equal(
        expected: expected,
        actual: FFmpeg.IsNetwork(source)
    );

    [Fact]
    public void Both_Pipes_Pin_The_Same_Stream_As_The_Probe()
    {
        // A multi-variant HLS master exposes every rendition as a stream and ffmpeg's default pick
        // is the largest — a different size than ParseProbe measured off the first one, which would
        // make the reader slice the raw pipe at the wrong frame boundary.
        string[] video = FFmpeg.VideoArgs(
            source: "https://host/master.m3u8",
            startSeconds: 0,
            fps: 30,
            speed: 1.0,
            maxHeight: 0
        ).ToArray();
        Assert.Equal(
            expected: "0:v:0",
            actual: video[Array.IndexOf(array: video, value: "-map") + 1]
        );

        // Optional, so a video-only source produces silence instead of failing the pipe.
        string[] audio = FFmpeg.AudioArgs(
            source: "https://host/master.m3u8",
            startSeconds: 0,
            speed: 1.0
        ).ToArray();
        Assert.Equal(
            expected: "0:a:0?",
            actual: audio[Array.IndexOf(array: audio, value: "-map") + 1]
        );
    }

    [Fact]
    public void An_Unmeasurable_Network_Source_Is_Live_And_Unseekable()
    {
        const string json = """
                            { "streams": [ { "codec_type": "video", "width": 1280, "height": 720,
                                             "r_frame_rate": "30/1" } ], "format": {} }
                            """;

        var live = FFmpeg.ParseProbe(source: "https://host/live.m3u8", json: json);
        Assert.True(live.IsLive);
        Assert.True(live.IsNetwork);
        Assert.False(live.IsSeekable); // no end to scrub to; the bar must not invent one

        // The same unmeasurable stream off a disk is a broken file, not a live feed.
        var file = FFmpeg.ParseProbe(source: "/media/clip.mp4", json: json);
        Assert.False(file.IsLive);
        Assert.False(file.IsNetwork);
    }

    [Fact]
    public void The_Frame_Ring_Is_Capped_In_Bytes_Not_Just_Seconds()
    {
        var player = new VideoPlayer { MaxBufferBytes = 64L * 1024 * 1024 };
        var hd = new MediaInfo(
            Source: "x",
            Duration: TimeSpan.FromMinutes(1),
            Video: new VideoTrackInfo(
                Width: 1920,
                Height: 1080,
                FrameRate: 30,
                Codec: "h264"
            ),
            Audio: null
        );

        // Two seconds of decoded 1080p RGBA is half a gigabyte; the byte cap is what stops the ring
        // from being sized in seconds at high resolutions.
        int frames = player.FrameQueueCapacity(media: hd, fps: 30);
        Assert.InRange(actual: frames, low: 3, high: 12);
        Assert.True(frames * 1920L * 1080 * 4 <= player.MaxBufferBytes);

        // Small frames hit the seconds target instead, and never fall below the floor.
        var small = new MediaInfo(
            Source: "x",
            Duration: TimeSpan.FromMinutes(1),
            Video: new VideoTrackInfo(
                Width: 320,
                Height: 180,
                FrameRate: 30,
                Codec: "h264"
            ),
            Audio: null
        );
        Assert.Equal(expected: 60, actual: player.FrameQueueCapacity(media: small, fps: 30));

        player.MaxBufferBytes = 1;
        Assert.Equal(expected: 3, actual: player.FrameQueueCapacity(media: hd, fps: 30));
    }

    /// <summary>
    ///     The settable signals are the transport, not a mirror of it: writing <c>Muted</c> reaches
    ///     the mixer, and each read-only signal notifies on its own so a bound control can subscribe
    ///     to just the one it draws.
    /// </summary>
    [Fact]
    public async Task Signals_Drive_The_Mixer_And_Notify_Individually()
    {
        Assert.SkipUnless(condition: FFmpeg.IsAvailable(), reason: "ffmpeg/ffprobe not installed");

        string path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote-vp-sig-{Environment.ProcessId}.mp4"
        );
        try
        {
            await RenderClip(path);

            var mixer = new RecordingAudioApi { Buffered = 1f };
            using var player = new VideoPlayer(mixer);

            int states = 0, positions = 0;
            using var stateSub = player.State.Observe(() => states++);
            using var positionSub = player.Position.Observe(() => positions++);

            await player.OpenAsync(source: path, ct: TestContext.Current.CancellationToken);
            Assert.True(
                condition: states > 0,
                userMessage: "opening did not report a state change"
            );

            player.Play();
            var deadline = Stopwatch.StartNew();
            while (positions == 0 && deadline.Elapsed.TotalSeconds < 20)
            {
                player.Tick();
                await Task.Delay(
                    millisecondsDelay: 16,
                    cancellationToken: TestContext.Current.CancellationToken
                );
            }

            // Position moved without State moving with it — that separation is what lets a bar
            // redraw one label per frame instead of its whole subtree.
            Assert.True(condition: positions > 0, userMessage: "the position signal never fired");

            player.Muted.Value = true;
            Assert.Equal(expected: 0f, actual: mixer.LastVolume);
            player.Muted.Value = false;
            player.Volume.Value = 0.25f;
            Assert.Equal(expected: 0.25f, actual: mixer.LastVolume, precision: 3);
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
        Assert.SkipUnless(condition: FFmpeg.IsAvailable(), reason: "ffmpeg/ffprobe not installed");

        string path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote-vp-audio-{Environment.ProcessId}.mp4"
        );
        try
        {
            await RenderClip(path);

            var mixer = new RecordingAudioApi { Buffered = 1f };
            using (var player = new VideoPlayer(mixer))
            {
                await player.OpenAsync(source: path, ct: TestContext.Current.CancellationToken);
                player.Play();

                // Audio buffers well before the first frame is presented, and the stream is only
                // started once both sides are primed — so wait for the start, not just the bytes.
                var deadline = Stopwatch.StartNew();
                while ((mixer.PushedBytes.Count < 4096 || !mixer.Played)
                       && deadline.Elapsed.TotalSeconds < 20)
                {
                    player.Tick();
                    await Task.Delay(
                        millisecondsDelay: 16,
                        cancellationToken: TestContext.Current.CancellationToken
                    );
                }

                Assert.NotEqual(expected: 0u, actual: mixer.Created);
                Assert.True(
                    condition: mixer.PushedBytes.Count > 44,
                    userMessage: "no PCM reached the mixer"
                );

                byte[] head = mixer.PushedBytes.Take(12).ToArray();
                Assert.Equal(
                    expected: "RIFF",
                    actual: Encoding.ASCII.GetString(bytes: head, index: 0, count: 4)
                );
                Assert.Equal(
                    expected: "WAVE",
                    actual: Encoding.ASCII.GetString(bytes: head, index: 8, count: 4)
                );
                Assert.True(condition: mixer.Played, userMessage: "the stream was never started");

                player.Pause();
                Assert.True(
                    condition: mixer.Stopped,
                    userMessage: "pausing did not stop the stream"
                );
            }

            // Disposing the player must hand the stream back, or a page that opens ten videos
            // leaks ten decoders into the mixer.
            Assert.Contains(expected: mixer.Created, collection: mixer.Destroyed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task RenderClip(string path)
    {
        var psi = new ProcessStartInfo(FFmpeg.FfmpegPath) {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] {
                     "-hide_banner",
                     "-loglevel",
                     "error",
                     "-nostdin",
                     "-y",
                     "-f",
                     "lavfi",
                     "-i",
                     "testsrc2=size=320x180:rate=25:duration=3",
                     "-f",
                     "lavfi",
                     "-i",
                     "sine=frequency=440:duration=3",
                     "-c:v",
                     "libx264",
                     "-preset",
                     "ultrafast",
                     "-pix_fmt",
                     "yuv420p",
                     "-c:a",
                     "aac",
                     "-shortest",
                     path,
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        string stderr =
            await proc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await proc.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(condition: proc.ExitCode == 0, userMessage: stderr);
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
        public float CursorSeconds { get; } = -1f;

        public int OutputRate => 48000;

        public uint CreateStream() => Created = 1;

        public int StreamPush(uint id, ReadOnlySpan<byte> bytes)
        {
            // The reader runs on its own thread; the assertions run on the test's.
            lock (_gate) PushedBytes.AddRange(bytes);
            return bytes.Length;
        }

        public void StreamFinish(uint id) => Finished = true;

        public AudioStreamState StreamStatus(uint id) => AudioStreamState.Playing;

        public float StreamBuffered(uint id) => Buffered;

        public void Play(uint id) => Played = true;

        public void Stop(uint id) => Stopped = true;

        public void Destroy(uint id) => Destroyed.Add(id);

        public float Cursor(uint id) => CursorSeconds;

        public bool AtEnd(uint id) => Finished;

        public uint CreateFile(string path, bool streaming) => 0;
        public void Seek(uint id, float seconds) { }
        public void ScheduleStart(uint id, float secondsFromNow) { }
        public float Duration(uint id) => -1f;
        public bool IsPlaying(uint id) => Played && !Stopped;
        public void SetVolume(uint id, float volume) => LastVolume = volume;

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
