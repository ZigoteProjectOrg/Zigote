using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Zigote.Videoplayer;

/// <summary>
///     The seam onto ffmpeg: where the binaries are, what a source contains, and the exact argument
///     lists the two decode pipes are started with.
///     <para>
///         The player drives the <c>ffmpeg</c> and <c>ffprobe</c> executables rather than linking
///         <c>libav*</c>. That buys the whole format and codec matrix — every container, every codec,
///         http/rtsp/rtmp inputs, hardware decoders — against a command-line contract that has been
///         stable for a decade, instead of against struct layouts that shift with each major
///         libavcodec soname. It costs one child process per stream and a pipe copy per frame.
///     </para>
///     <para>
///         Argument construction lives here as pure functions over
///         <see cref="ProcessStartInfo.ArgumentList" /> — no string concatenation, so a path with a
///         space or a quote in it is not a parsing question, and the lines are assertable in tests
///         without spawning anything.
///     </para>
/// </summary>
public static class FFmpeg
{
    /// <summary>PCM handed to the engine's push stream. 48 kHz stereo is what the mixer wants anyway.</summary>
    public const int AudioSampleRate = 48000;

    public const int AudioChannels = 2;
    internal const int AudioBytesPerFrame = AudioChannels * 2; // s16

    /// <summary>Ceiling on the output frame rate. A 1000 fps source is a decode bomb, not a video.</summary>
    private const double MaxFrameRate = 120.0;

    /// <summary>
    ///     The <c>ffmpeg</c> binary — a bare name resolved through <c>PATH</c>, or an absolute path.
    ///     Overridable for an app that ships its own build; <c>ZIGOTE_FFMPEG</c> sets the default.
    /// </summary>
    public static string FfmpegPath { get; set; } =
        Environment.GetEnvironmentVariable("ZIGOTE_FFMPEG") ?? "ffmpeg";

    /// <summary>The <c>ffprobe</c> binary; <c>ZIGOTE_FFPROBE</c> sets the default.</summary>
    public static string FfprobePath { get; set; } =
        Environment.GetEnvironmentVariable("ZIGOTE_FFPROBE") ?? "ffprobe";

    /// <summary>
    ///     Whether <see cref="FfmpegPath" /> and <see cref="FfprobePath" /> can actually be started.
    ///     Worth calling once at startup so a missing install is a clear message on your own screen
    ///     rather than a <see cref="PlaybackState.Failed" /> the first time someone opens a video.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo(fileName: FfprobePath, arguments: "-version") {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            if (p is null) return false;
            p.WaitForExit(5000);
            return true;
        }
        catch (Exception)
        {
            // Missing binary, denied exec bit, sandbox — all the same answer to the caller.
            return false;
        }
    }

    /// <summary>
    ///     Read a source's duration, video geometry and stream list. Runs <c>ffprobe</c>, so it takes
    ///     a container parse (and a network round trip for a URL) — it is async for a reason.
    /// </summary>
    /// <exception cref="InvalidOperationException">ffprobe failed or reported no playable stream.</exception>
    public static async Task<MediaInfo> ProbeAsync(string source, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var psi = new ProcessStartInfo(FfprobePath) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in ProbeArgs(source)) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException(
                             $"Could not start '{FfprobePath}'."
                         );

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        string json = await stdout.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffprobe failed for '{source}': {Tail(await stderr.ConfigureAwait(false))}"
            );
        }

        return ParseProbe(source: source, json: json);
    }

    internal static IEnumerable<string> ProbeArgs(string source)
    {
        var args = new List<string> {
            "-v",
            "error",
        };
        AddNetworkOptions(args: args, source: source);
        args.Add("-print_format");
        args.Add("json");
        args.Add("-show_format");
        args.Add("-show_streams");
        args.Add(source);
        return args;
    }

    /// <summary>
    ///     Parse ffprobe's JSON into a <see cref="MediaInfo" />. Separate from the process so the
    ///     mapping — and its fallbacks for the fields ffprobe leaves out — is testable on a fixture.
    /// </summary>
    internal static MediaInfo ParseProbe(string source, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        VideoTrackInfo? video = null;
        AudioTrackInfo? audio = null;
        double streamDuration = 0.0;

        if (root.TryGetProperty(propertyName: "streams", value: out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string kind = Str(obj: s, name: "codec_type");
                streamDuration = Math.Max(
                    val1: streamDuration,
                    val2: Num(Str(obj: s, name: "duration"))
                );

                // Cover art is an "attached_pic" video stream of one frame. Treating it as the video
                // track would show an mp3's album cover as a 1-frame film and hang the clock on it.
                if (kind == "video" && video is null && !IsAttachedPicture(s))
                {
                    video = new VideoTrackInfo(
                        Width: Int(obj: s, name: "width"),
                        Height: Int(obj: s, name: "height"),
                        FrameRate: FrameRate(s),
                        Codec: Str(obj: s, name: "codec_name")
                    );
                }
                else if (kind == "audio" && audio is null)
                {
                    audio = new AudioTrackInfo(
                        Channels: Int(obj: s, name: "channels"),
                        SampleRate: Int(obj: s, name: "sample_rate"),
                        Codec: Str(obj: s, name: "codec_name"),
                        Language: Language(s)
                    );
                }
            }
        }

        double duration = 0.0;
        if (root.TryGetProperty(propertyName: "format", value: out var format))
            duration = Num(Str(obj: format, name: "duration"));
        if (duration <= 0) duration = streamDuration;

        if (video is null && audio is null)
            throw new InvalidOperationException($"No audio or video stream in '{source}'.");

        return new MediaInfo(
            Source: source,
            Duration: TimeSpan.FromSeconds(Math.Max(val1: 0, val2: duration)),
            Video: video,
            Audio: audio,
            // A network source ffprobe could not measure is a live one: no end to seek to, and the
            // transport must not offer a scrubber over a length it invented.
            IsLive: IsNetwork(source) && duration <= 0
        );
    }

    /// <summary>
    ///     The video pipe: decode from <paramref name="startSeconds" /> and write bare RGBA frames to
    ///     stdout at a fixed rate, so frame <c>N</c> is at output second <c>N / fps</c> with no
    ///     timestamps to parse. <c>-ss</c> ahead of <c>-i</c> is the fast seek — ffmpeg jumps to the
    ///     preceding keyframe and decodes forward to the exact frame.
    /// </summary>
    /// <param name="maxHeight">Downscale anything taller (0 = native). A 4K frame is 33 MB per copy.</param>
    internal static IEnumerable<string> VideoArgs(
        string source,
        double startSeconds,
        double fps,
        double speed,
        int maxHeight)
    {
        var args = new List<string> {
            "-hide_banner",
            "-loglevel",
            "error",
            "-nostdin",
        };
        AddNetworkOptions(args: args, source: source);
        if (startSeconds > 0)
        {
            args.Add("-ss");
            args.Add(Fmt(startSeconds));
        }

        args.Add("-i");
        args.Add(source);
        // Pin the stream instead of taking ffmpeg's default pick. A multi-variant HLS master
        // playlist exposes every rendition as its own stream, and ffmpeg's default is the largest
        // — which would not be the one ProbeParse measured, so the raw frames arriving down the
        // pipe would be a different size than the reader is slicing them at. First video stream,
        // both here and there.
        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");

        var filters = new List<string>();
        // setpts compresses the timeline, and the CFR rate below then resamples it: at 2x, a 25 fps
        // source still emits 25 fps of output covering twice the media. That is what keeps "output
        // frame N is at output second N/fps" true at every speed.
        if (Math.Abs(speed - 1.0) > 1e-6)
            filters.Add($"setpts=PTS/{Fmt(speed)}");
        if (maxHeight > 0)
            // -2 keeps the width even (yuv/h264 chroma alignment) and preserves the aspect.
            filters.Add($"scale=-2:min({maxHeight}\\,ih)");
        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(separator: ',', values: filters));
        }

        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("rgba");
        args.Add("-fps_mode");
        args.Add("cfr");
        args.Add("-r");
        args.Add(Fmt(fps));
        args.Add("pipe:1");
        return args;
    }

    /// <summary>
    ///     The audio pipe: headerless little-endian 16-bit PCM at the mixer's rate. Raw rather than
    ///     <c>-f wav</c> because the stream restarts on every seek and speed change, and a second RIFF
    ///     header arriving mid-stream is garbage to the decoder — this way the header is ours to write
    ///     exactly once per stream (see <see cref="WavHeader" />).
    /// </summary>
    internal static IEnumerable<string> AudioArgs(string source, double startSeconds, double speed)
    {
        var args = new List<string> {
            "-hide_banner",
            "-loglevel",
            "error",
            "-nostdin",
        };
        AddNetworkOptions(args: args, source: source);
        if (startSeconds > 0)
        {
            args.Add("-ss");
            args.Add(Fmt(startSeconds));
        }

        args.Add("-i");
        args.Add(source);
        // Trailing '?' = optional: a video-only source simply produces no audio rather than failing
        // the whole pipe.
        args.Add("-map");
        args.Add("0:a:0?");
        args.Add("-vn");
        args.Add("-sn");
        args.Add("-dn");

        if (Math.Abs(speed - 1.0) > 1e-6)
        {
            args.Add("-filter:a");
            args.Add(ATempo(speed));
        }

        args.Add("-f");
        args.Add("s16le");
        args.Add("-acodec");
        args.Add("pcm_s16le");
        args.Add("-ar");
        args.Add(AudioSampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-ac");
        args.Add(AudioChannels.ToString(CultureInfo.InvariantCulture));
        args.Add("pipe:1");
        return args;
    }

    /// <summary>
    ///     Time-stretch without shifting pitch. One <c>atempo</c> covers [0.5, 2]; outside it they
    ///     chain, because a chipmunk at 4x is a bug report.
    /// </summary>
    internal static string ATempo(double speed)
    {
        var stages = new List<string>();
        double remaining = speed;
        while (remaining > 2.0)
        {
            stages.Add("atempo=2.0");
            remaining /= 2.0;
        }

        while (remaining < 0.5)
        {
            stages.Add("atempo=0.5");
            remaining /= 0.5;
        }

        stages.Add($"atempo={Fmt(remaining)}");
        return string.Join(separator: ',', values: stages);
    }

    /// <summary>
    ///     A canonical 44-byte PCM WAV header for the stream the audio pipe produces. The engine's
    ///     push source is fed <i>encoded</i> bytes and detects the container itself, so raw PCM needs
    ///     a header in front of it; the declared data length is the largest block-aligned value that
    ///     fits the 32-bit field, since the real length is not known until the source ends.
    /// </summary>
    internal static byte[] WavHeader()
    {
        const uint dataSize = 0x7FFFFFFF & ~(uint)(AudioBytesPerFrame - 1);
        byte[] h = new byte[44];
        var span = h.AsSpan();

        "RIFF"u8.CopyTo(span);
        BitConverter.TryWriteBytes(destination: span[4..], value: 36u + dataSize);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BitConverter.TryWriteBytes(destination: span[16..], value: 16u); // PCM fmt chunk size
        BitConverter.TryWriteBytes(destination: span[20..], value: (ushort)1); // PCM
        BitConverter.TryWriteBytes(destination: span[22..], value: (ushort)AudioChannels);
        BitConverter.TryWriteBytes(destination: span[24..], value: (uint)AudioSampleRate);
        BitConverter.TryWriteBytes(
            destination: span[28..],
            value: (uint)(AudioSampleRate * AudioBytesPerFrame)
        );
        BitConverter.TryWriteBytes(destination: span[32..], value: (ushort)AudioBytesPerFrame);
        BitConverter.TryWriteBytes(destination: span[34..], value: (ushort)16); // bits per sample
        "data"u8.CopyTo(span[36..]);
        BitConverter.TryWriteBytes(destination: span[40..], value: dataSize);
        return h;
    }

    /// <summary>
    ///     Whether a source is opened over the network rather than off a disk. Governs the reconnect
    ///     options below and, with an unknown duration, whether the source is treated as live.
    /// </summary>
    public static bool IsNetwork(string source)
    {
        return Uri.TryCreate(uriString: source, uriKind: UriKind.Absolute, result: out var uri)
               && uri.Scheme is "http" or "https" or "rtmp" or "rtmps" or "rtsp" or "udp" or "tcp"
                   or "srt";
    }

    /// <summary>
    ///     Survive a dropped connection instead of ending the stream. These are options on ffmpeg's
    ///     http protocol, so they are only emitted for http(s) — passing them to a file or an rtsp
    ///     input is an "option not found" error, not a no-op.
    ///     <para>
    ///         Without them a momentary DNS blip or a 5xx from a CDN edge ends the read, the pipe
    ///         closes, and the player reports a finished video. With them ffmpeg re-opens with a
    ///         range request and playback continues.
    ///     </para>
    /// </summary>
    internal static void AddNetworkOptions(List<string> args, string source)
    {
        if (!Uri.TryCreate(
                uriString: source,
                uriKind: UriKind.Absolute,
                result: out var uri
            )) return;
        if (uri.Scheme is not ("http" or "https")) return;

        args.Add("-reconnect");
        args.Add("1");
        args.Add("-reconnect_streamed"); // live/chunked bodies, which cannot be range-resumed
        args.Add("1");
        args.Add("-reconnect_on_network_error");
        args.Add("1");
        args.Add("-reconnect_delay_max"); // give up after this many seconds of backoff
        args.Add("8");
        args.Add("-multiple_requests"); // keep the connection alive across range requests
        args.Add("1");
    }

    /// <summary>Clamp a probed frame rate into something a decode loop can survive.</summary>
    internal static double SaneFrameRate(double fps) => !double.IsFinite(fps) || fps <= 0
        ? 25.0
        : Math.Min(val1: fps, val2: MaxFrameRate);

    /// <summary>Keep the tail of an ffmpeg stderr dump — the last lines are the ones that say why.</summary>
    internal static string Tail(string text, int max = 400)
    {
        text = text.Trim();
        return text.Length <= max ? text : "…" + text[^max..];
    }

    private static bool IsAttachedPicture(JsonElement stream)
    {
        return stream.TryGetProperty(propertyName: "disposition", value: out var d)
               && d.TryGetProperty(propertyName: "attached_pic", value: out var v)
               && v.TryGetInt32(out int i)
               && i != 0;
    }

    private static double FrameRate(JsonElement stream)
    {
        double fps = Fraction(Str(obj: stream, name: "r_frame_rate"));
        if (fps <= 0) fps = Fraction(Str(obj: stream, name: "avg_frame_rate"));
        return SaneFrameRate(fps);
    }

    /// <summary>ffprobe reports rates as exact rationals ("30000/1001"); 0/0 means "it would not say".</summary>
    internal static double Fraction(string value)
    {
        int slash = value.IndexOf('/');
        if (slash < 0) return Num(value);
        double num = Num(value[..slash]);
        double den = Num(value[(slash + 1)..]);
        return den > 0 ? num / den : 0;
    }

    private static string Language(JsonElement stream) =>
        stream.TryGetProperty(propertyName: "tags", value: out var tags)
            ? Str(obj: tags, name: "language")
            : "";

    private static string Str(JsonElement obj, string name)
    {
        return obj.TryGetProperty(propertyName: name, value: out var v) &&
               v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
    }

    private static int Int(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(propertyName: name, value: out var v)) return 0;
        return v.ValueKind switch {
            JsonValueKind.Number => v.TryGetInt32(out int i) ? i : 0,
            JsonValueKind.String => (int)Num(v.GetString() ?? ""),
            _ => 0,
        };
    }

    private static double Num(string value)
    {
        return double.TryParse(
                   s: value,
                   style: NumberStyles.Float,
                   provider: CultureInfo.InvariantCulture,
                   result: out double d
               )
               && double.IsFinite(d)
            ? d
            : 0;
    }

    /// <summary>Invariant formatting: ffmpeg parses "1.5", never the "1,5" a German locale would emit.</summary>
    private static string Fmt(double value) => value.ToString(
        format: "0.######",
        provider: CultureInfo.InvariantCulture
    );
}
