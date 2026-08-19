using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Camera;

/// <summary>
///     Desktop capture — Windows, macOS and Linux, the base <c>net10.0</c> build — by driving the
///     <c>ffmpeg</c> executable over the OS capture input (<c>v4l2</c> / <c>avfoundation</c> /
///     <c>dshow</c>), the same trade <c>Zigote.Videoplayer</c> makes for decoding: one child
///     process and a pipe copy per frame buys every camera the OS can see, against a command-line
///     contract instead of three native capture APIs. Frames arrive as raw RGBA on stdout and go
///     straight into the mailbox; the newest one wins.
/// </summary>
internal static partial class CameraDriver
{
    /// <summary>Same override knobs as Zigote.Videoplayer, so one env var configures both.</summary>
    private static readonly string FfmpegPath =
        Environment.GetEnvironmentVariable("ZIGOTE_FFMPEG") ?? "ffmpeg";

    private static readonly string FfprobePath =
        Environment.GetEnvironmentVariable("ZIGOTE_FFPROBE") ?? "ffprobe";

    /// <summary>Desktop OSes gate camera access at the OS level (or not at all) — nothing to prompt.</summary>
    public static Task<bool> RequestPermissionAsync() => Task.FromResult(true);

    public static void OnAppLifecycle(bool paused)
    {
    }

    public static Task<CameraDeviceInfo[]> GetDevicesAsync()
    {
        if (OperatingSystem.IsLinux()) return Task.FromResult(LinuxDevices());
        return Task.Run(() =>
            {
                string stderr = ListDevicesStderr();
                return OperatingSystem.IsMacOS()
                    ? ParseAvFoundationDevices(stderr)
                    : ParseDshowDevices(stderr);
            }
        );
    }

    /// <param name="minimalProcessing">
    ///     No-op here: v4l2/avfoundation/dshow already hand over the driver's output untouched —
    ///     the only thing this pipeline ever does to a frame is the optional downscale.
    /// </param>
    public static ICameraSession Open(
        string? deviceId,
        int maxHeight,
        bool minimalProcessing,
        FrameMailbox frames,
        Action<string> onError) =>
        new FfmpegSession(deviceId: deviceId, maxHeight: maxHeight, frames: frames, onError: onError);

    /// <summary>One more ffmpeg invocation: raw RGBA in on stdin, a single JPEG out on stdout.</summary>
    public static async Task<byte[]> EncodeJpegAsync(byte[] rgba, int width, int height, int quality)
    {
        var psi = Silent(FfmpegPath);
        psi.RedirectStandardInput = true;
        foreach (string a in JpegArgs(width: width, height: height, quality: quality))
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException($"Could not start '{FfmpegPath}'.");

        var stdout = new MemoryStream();
        var reading = proc.StandardOutput.BaseStream.CopyToAsync(stdout);
        var errors = proc.StandardError.ReadToEndAsync();
        await proc.StandardInput.BaseStream.WriteAsync(rgba.AsMemory(start: 0, length: width * height * 4))
            .ConfigureAwait(false);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync().ConfigureAwait(false);
        await reading.ConfigureAwait(false);

        if (proc.ExitCode != 0 || stdout.Length == 0)
            throw new InvalidOperationException(
                $"JPEG encode failed: {Tail(await errors.ConfigureAwait(false))}"
            );
        return stdout.ToArray();
    }

    // ── argument construction: pure, assertable without spawning anything ───────

    internal static IEnumerable<string> CaptureArgs(string deviceId, int width, int height)
    {
        var args = new List<string> {
            "-hide_banner",
            "-loglevel",
            "error",
            "-nostdin",
        };
        args.AddRange(InputArgs(deviceId));
        args.Add("-vf");
        // Exact output geometry, whatever the device negotiated: rawvideo is headerless, so the
        // reader slicing frames at width*height*4 must never be wrong about either number.
        args.Add($"scale={width}:{height}");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("rgba");
        args.Add("pipe:1");
        return args;
    }

    internal static IEnumerable<string> ProbeArgs(string deviceId)
    {
        var args = new List<string> {
            "-v",
            "error",
            "-print_format",
            "json",
            "-show_streams",
        };
        args.AddRange(InputArgs(deviceId)); // -f <capture format> … -i <device>
        return args;
    }

    internal static IEnumerable<string> JpegArgs(int width, int height, int quality)
    {
        // mjpeg qscale runs 2 (best) to 31 (worst); map the familiar 1–100 onto it.
        int q = 2 + (int)Math.Round((100 - quality) * 29 / 99.0);
        return
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "rgba",
            "-video_size", $"{width}x{height}",
            "-i", "pipe:0",
            "-frames:v", "1",
            "-c:v", "mjpeg", "-q:v", q.ToString(CultureInfo.InvariantCulture),
            "-f", "image2", "pipe:1",
        ];
    }

    private static string InputFormat() =>
        OperatingSystem.IsMacOS() ? "avfoundation" :
        OperatingSystem.IsWindows() ? "dshow" : "v4l2";

    private static IEnumerable<string> InputArgs(string deviceId)
    {
        var args = new List<string> {
            "-f",
            InputFormat(),
        };
        if (OperatingSystem.IsMacOS())
        {
            // avfoundation opens at whatever it feels like unless pinned; 720p30 is universal on
            // Mac cameras. ponytail: no per-device mode probe — an unsupported request fails with
            // ffmpeg listing the supported modes in Error; enumerate modes if that ever bites.
            args.Add("-framerate");
            args.Add("30");
            args.Add("-video_size");
            args.Add("1280x720");
        }

        args.Add("-i");
        args.Add(OperatingSystem.IsWindows() ? $"video={deviceId}" : deviceId);
        return args;
    }

    // ── device discovery ────────────────────────────────────────────────────────

    private static CameraDeviceInfo[] LinuxDevices()
    {
        var devices = new List<(int Node, CameraDeviceInfo Info)>();
        foreach (string dir in Directory.Exists("/sys/class/video4linux")
                     ? Directory.GetDirectories("/sys/class/video4linux")
                     : [])
        {
            string entry = Path.GetFileName(dir);
            if (!entry.StartsWith("video") || !int.TryParse(s: entry[5..], result: out int node))
                continue;
            // A UVC camera exposes extra non-capture nodes (metadata); those carry index != 0.
            // ponytail: heuristic, not ioctl truth — query V4L2 capabilities if a capture node
            // with a nonzero index ever shows up in the wild.
            if (ReadTrimmed(Path.Combine(path1: dir, path2: "index")) is not ("0" or null))
                continue;
            string name = ReadTrimmed(Path.Combine(path1: dir, path2: "name")) ?? entry;
            devices.Add((node, new CameraDeviceInfo(
                Id: "/dev/" + entry,
                Name: name,
                Facing: CameraFacing.External
            )));
        }

        return devices.OrderBy(d => d.Node).Select(d => d.Info).ToArray();

        static string? ReadTrimmed(string path)
        {
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>ffmpeg prints device lists to stderr and exits nonzero; the text is the answer.</summary>
    private static string ListDevicesStderr()
    {
        var psi = Silent(FfmpegPath);
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-list_devices");
        psi.ArgumentList.Add("true");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(InputFormat());
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(OperatingSystem.IsMacOS() ? "" : "dummy");

        using var proc = Process.Start(psi);
        if (proc is null) return "";
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10_000);
        return stderr;
    }

    /// <summary>
    ///     <c>[AVFoundation ...] [0] FaceTime HD Camera</c> lines between the "video devices" and
    ///     "audio devices" headers. Screen-capture pseudo-devices are not cameras.
    /// </summary>
    internal static CameraDeviceInfo[] ParseAvFoundationDevices(string stderr)
    {
        var devices = new List<CameraDeviceInfo>();
        bool inVideo = false;
        foreach (string line in stderr.Split('\n'))
        {
            if (line.Contains("video devices:")) inVideo = true;
            else if (line.Contains("audio devices:")) break;
            else if (inVideo)
            {
                var m = AvDeviceLine().Match(line);
                if (!m.Success || m.Groups[2].Value.StartsWith("Capture screen")) continue;
                devices.Add(new CameraDeviceInfo(
                    Id: m.Groups[1].Value,
                    Name: m.Groups[2].Value.Trim(),
                    Facing: CameraFacing.External
                ));
            }
        }

        return devices.ToArray();
    }

    /// <summary><c>[dshow ...] "HD WebCam" (video)</c> lines; the quoted name is the ffmpeg id.</summary>
    internal static CameraDeviceInfo[] ParseDshowDevices(string stderr)
    {
        var devices = new List<CameraDeviceInfo>();
        foreach (string line in stderr.Split('\n'))
        {
            var m = DshowDeviceLine().Match(line);
            if (m.Success)
                devices.Add(new CameraDeviceInfo(
                    Id: m.Groups[1].Value,
                    Name: m.Groups[1].Value,
                    Facing: CameraFacing.External
                ));
        }

        return devices.ToArray();
    }

    [GeneratedRegex(@"\[(\d+)\]\s+(.+)$")]
    private static partial Regex AvDeviceLine();

    [GeneratedRegex("\"([^\"]+)\"\\s+\\(video\\)")]
    private static partial Regex DshowDeviceLine();

    // ── the session ─────────────────────────────────────────────────────────────

    private sealed class FfmpegSession : ICameraSession
    {
        private readonly FrameMailbox _frames;
        private readonly Action<string> _onError;
        private readonly int _maxHeight;
        private readonly string? _deviceId;
        private volatile bool _cancelled;
        private volatile Process? _proc;

        public FfmpegSession(string? deviceId, int maxHeight, FrameMailbox frames, Action<string> onError)
        {
            _deviceId = deviceId;
            _maxHeight = maxHeight;
            _frames = frames;
            _onError = onError;
            var t = new Thread(Run) {
                IsBackground = true,
                Name = "zigote-camera-capture",
            };
            t.Start();
        }

        public void Dispose()
        {
            _cancelled = true;
            Kill(_proc);
        }

        /// <summary>Resolve the device, learn its geometry, then read frames until stopped.</summary>
        private void Run()
        {
            try
            {
                string device = _deviceId ?? DefaultDevice()
                    ?? throw new InvalidOperationException("No camera found.");

                (int nativeW, int nativeH) = OperatingSystem.IsMacOS()
                    ? (1280, 720) // pinned by InputArgs; a probe would need the same pin anyway
                    : Probe(device);
                (int w, int h) = OutputSize(nativeWidth: nativeW, nativeHeight: nativeH, maxHeight: _maxHeight);

                if (_cancelled) return;
                Capture(device: device, width: w, height: h);
            }
            catch (Exception ex)
            {
                if (!_cancelled) _onError(ex.Message);
            }
        }

        private void Capture(string device, int width, int height)
        {
            var psi = Silent(FfmpegPath);
            foreach (string a in CaptureArgs(deviceId: device, width: width, height: height))
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException($"Could not start '{FfmpegPath}'.");
            _proc = proc;

            // Drained concurrently: a full stderr pipe stalls ffmpeg, and its tail is the only
            // explanation when the device cannot be opened.
            var stderr = new StringBuilder();
            var errPump = Task.Run(() =>
                {
                    string? line;
                    while ((line = proc.StandardError.ReadLine()) is not null)
                        lock (stderr)
                        {
                            if (stderr.Length < 4096) stderr.AppendLine(line);
                        }
                }
            );

            var stdout = proc.StandardOutput.BaseStream;
            int frameBytes = width * height * 4;
            bool gotFrame = false;

            while (!_cancelled)
            {
                byte[] buffer = _frames.Rent(frameBytes);
                try
                {
                    stdout.ReadExactly(buffer: buffer, offset: 0, count: frameBytes);
                }
                catch (Exception)
                {
                    _frames.Return(buffer);
                    break;
                }

                _frames.Publish(buffer: buffer, width: width, height: height);
                gotFrame = true;
            }

            _proc = null;
            if (_cancelled) return;

            // EOF without a stop is a failure either way: the device would not open, or the
            // stream died mid-run.
            errPump.Wait(1000);
            string errors;
            lock (stderr)
            {
                errors = stderr.ToString();
            }

            _onError(errors.Trim().Length > 0
                ? Tail(errors)
                : gotFrame
                    ? "Camera stream ended unexpectedly."
                    : $"Camera '{device}' produced no frames."
            );
        }

        private static string? DefaultDevice()
        {
            if (OperatingSystem.IsMacOS()) return "0";
            var devices = OperatingSystem.IsLinux()
                ? LinuxDevices()
                : ParseDshowDevices(ListDevicesStderr());
            return devices.FirstOrDefault()?.Id;
        }

        /// <summary>Ask ffprobe what the device emits when opened with defaults — the capture run
        /// opens it the same way, so the geometry matches.</summary>
        private static (int Width, int Height) Probe(string device)
        {
            var psi = Silent(FfprobePath);
            foreach (string a in ProbeArgs(device)) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException($"Could not start '{FfprobePath}'.");
            string json = proc.StandardOutput.ReadToEnd();
            string errors = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"Cannot open camera '{device}': {Tail(errors)}");

            using var doc = JsonDocument.Parse(json);
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                if (s.TryGetProperty(propertyName: "width", value: out var wEl)
                    && s.TryGetProperty(propertyName: "height", value: out var hEl)
                    && wEl.TryGetInt32(out int w) && hEl.TryGetInt32(out int h)
                    && w > 0 && h > 0)
                    return (w, h);
            }

            throw new InvalidOperationException($"Camera '{device}' reported no video geometry.");
        }
    }

    /// <summary>Scale down to the height cap, aspect preserved, width even — never upscale.</summary>
    internal static (int Width, int Height) OutputSize(int nativeWidth, int nativeHeight, int maxHeight)
    {
        if (maxHeight <= 0 || nativeHeight <= maxHeight || nativeHeight <= 0)
            return (nativeWidth, nativeHeight);

        double scale = (double)maxHeight / nativeHeight;
        int width = (int)Math.Round(nativeWidth * scale / 2.0) * 2;
        return (Math.Max(val1: 2, val2: width), maxHeight);
    }

    private static ProcessStartInfo Silent(string fileName) => new(fileName) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

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
    }

    private static string Tail(string text, int max = 400)
    {
        text = text.Trim();
        return text.Length <= max ? text : "…" + text[^max..];
    }
}
