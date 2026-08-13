using System.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.Videoplayer;
using static Gallery.GalleryUi;
using Switch = Zigote.UI.Material.Switch;

namespace Gallery;

/// <summary>
///     The ffmpeg-backed video player: a <see cref="VideoView" /> with its transport, plus the fit
///     and loop switches and whatever ffprobe found in the source.
///     <para>
///         No clip is committed to the repo — the page renders its own with ffmpeg's test pattern
///         generator the first time it is opened, and caches it in the temp directory. "Open a
///         file…" plays anything else.
///     </para>
///     <para>
///         Built once and mutated: changing the fit or opening a new source writes into the retained
///         view and label rather than rebuilding the page, so the scroll position and every control's
///         state survive.
///     </para>
/// </summary>
internal sealed class VideoPage : ComposedWidget
{
    /// <summary>
    ///     Public test assets, both served over https so the reconnect path and the buffered-ahead
    ///     readout have something real to work against. The first is a progressive MP4 (Big Buck
    ///     Bunny, CC-BY, Blender Foundation); the second is a multi-variant HLS master playlist,
    ///     which is the case where ffmpeg's default stream pick and ffprobe's first stream differ.
    /// </summary>
    private const string HttpsSample =
        "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4";

    private const string HlsSample = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8";

    private static readonly string[] VideoExtensions =
        ["mp4", "mkv", "webm", "mov", "avi", "m4v", "mp3", "flac", "wav", "ogg"];

    /// <summary>Decode caps offered by the quality selector; 0 = whatever the source is.</summary>
    private static readonly (string Label, int MaxHeight)[] Qualities =
        [("360p", 360), ("540p", 540), ("720p", 720), ("Source", 0)];

    private readonly VideoPlayer _player = new(ZigoteEngine.Instance?.Audio);
    private readonly Text _sourceLabel = new("Preparing the sample clip…");
    private readonly List<IDisposable> _subscriptions = [];

    private int _quality = 1; // 540p — matches the 260 px pane without wasting bandwidth
    private Widget? _root;

    /// <summary>What is open, so the quality selector can re-open the same thing at a new cap.</summary>
    private string? _source;

    /// <summary>Set by this page for things the player never sees (a missing ffmpeg, a cancelled pick).</summary>
    private string? _status;

    private int _uiThread;
    private VideoView? _view;

    protected override void OnMount()
    {
        _uiThread = Environment.CurrentManagedThreadId;
        _subscriptions.Add(_player.Media.Observe(() => OnUi(SyncSource)));
        _subscriptions.Add(_player.Error.Observe(() => OnUi(SyncSource)));
        LoadSample();
    }

    protected override void OnUnmount()
    {
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();
        _player.Dispose();
    }

    protected override Widget Build(BuildContext context)
    {
        if (_root is not null) return _root;

        _view = new VideoView(_player) { AltText = "Demo video" };

        return _root = Sections(
            Section(
                title: "Player",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new SizedBox(height: 260, child: _view),
                        new SizedBox(height: 8),
                        new VideoControls(_player),
                    ]
                )
            ),
            Section(
                title: "Source",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        _sourceLabel,
                        new SizedBox(height: 12),
                        new Wrap(
                            spacing: 12,
                            runSpacing: 8,
                            children: [
                                new OutlinedButton(
                                    child: new Text("Open a file…"),
                                    onPressed: OpenFile
                                ),
                                new OutlinedButton(
                                    child: new Text("Local sample"),
                                    onPressed: LoadSample
                                ),
                                new OutlinedButton(
                                    child: new Text("Stream over HTTPS"),
                                    onPressed: () => Load(HttpsSample)
                                ),
                                new OutlinedButton(
                                    child: new Text("Stream HLS"),
                                    onPressed: () => Load(HlsSample)
                                ),
                            ]
                        ),
                    ]
                )
            ),
            Section(
                title: "Decode quality",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new Text(
                            "Caps the decoded height. Every frame crosses a pipe and an upload "
                            + "whole, so a 4K source drawn in a 260 px pane costs 60× the bandwidth "
                            + "of one that was capped."
                        ),
                        new SizedBox(height: 12),
                        new SegmentedControl(
                            segments: Qualities.Select(q => q.Label),
                            selected: _quality,
                            onChanged: SetQuality
                        ),
                    ]
                )
            ),
            Section(
                title: "Fit & transport",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        // Both write straight through — no page rebuild, so the scroll stays put.
                        new SegmentedControl(
                            segments: ["Contain", "Cover", "Fill"],
                            selected: 0,
                            onChanged: i =>
                            {
                                if (_view is not null) _view.Fit = (VideoFit)i;
                            }
                        ),
                        new SizedBox(height: 12),
                        LabeledRow(
                            control: new Switch(
                                value: _player.Loop.Value,
                                onChanged: v => _player.Loop.Value = v
                            ),
                            label: "Loop"
                        ),
                    ]
                )
            )
        );
    }

    private void SyncSource() => _sourceLabel.Text = _status ?? _player.Error.Value ?? Describe();

    private string Describe()
    {
        var media = _player.Media.Value;
        if (media is null) return "Preparing the sample clip…";

        string video = media.Video is { } v
            ? $"{v.Width}×{v.Height} {v.Codec} @ {v.FrameRate:0.##} fps"
            : "no video";
        string audio = media.Audio is { } a
            ? $"{a.Codec} {a.SampleRate} Hz ×{a.Channels}"
            : "no audio";

        string name = media.IsNetwork ? media.Source : Path.GetFileName(media.Source);
        string length = media.IsLive ? "live" : $"{media.Duration:mm\\:ss}";
        string transport = media.IsNetwork ? " · streaming" : "";
        return $"{name}\n{video} · {audio} · {length}{transport}";
    }

    private void OpenFile()
    {
        var app = App.Active;
        if (app is null) return;

        Run();
        return;

        async void Run()
        {
            try
            {
                string[] picked = await FileBrowserDialog.ShowAsync(
                    app: app,
                    options: new FileBrowserOptions {
                        Kind = FileDialogKind.OpenFile,
                        Title = "Open a video",
                        Filters =
                            [new FileDialogFilter(name: "Media", extensions: VideoExtensions)],
                    }
                );
                if (picked.Length > 0) await Open(picked[0]);
            }
            catch (Exception ex)
            {
                Report(ex.Message);
            }
        }
    }

    private void LoadSample()
    {
        Run();
        return;

        async void Run()
        {
            if (!FFmpeg.IsAvailable())
            {
                Report(
                    "ffmpeg and ffprobe were not found on PATH — install them, or point "
                    + "ZIGOTE_FFMPEG / ZIGOTE_FFPROBE at a build."
                );
                return;
            }

            try
            {
                await Open(await SampleClipAsync());
            }
            catch (Exception ex)
            {
                Report(ex.Message);
            }
        }
    }

    /// <summary>Fire-and-forget open, for the buttons.</summary>
    private void Load(string source)
    {
        Run();
        return;

        async void Run()
        {
            try
            {
                await Open(source);
            }
            catch (Exception ex)
            {
                Report(ex.Message);
            }
        }
    }

    private async Task Open(string source, TimeSpan resumeAt = default)
    {
        Report(null);
        _source = source;
        await _player.OpenAsync(source: source, maxHeight: Qualities[_quality].MaxHeight);
        if (resumeAt > TimeSpan.Zero) _player.Seek(resumeAt);
    }

    /// <summary>
    ///     Re-open the current source at a new decode cap, landing back where playback was — the cap
    ///     is a property of the pipeline, so there is nothing to change in place.
    /// </summary>
    private void SetQuality(int index)
    {
        if (index == _quality || _source is null) return;
        _quality = index;

        var resumeAt = _player.Position.Value;
        bool wasPlaying = _player.IsPlaying;
        string source = _source;

        Run();
        return;

        async void Run()
        {
            try
            {
                await Open(source: source, resumeAt: resumeAt);
                if (wasPlaying) _player.Play();
            }
            catch (Exception ex)
            {
                Report(ex.Message);
            }
        }
    }

    private void Report(string? message)
    {
        _status = message;
        OnUi(SyncSource);
    }

    /// <summary>Open and load run on worker threads; the label they write lives on the UI thread.</summary>
    private void OnUi(Action action)
    {
        if (Environment.CurrentManagedThreadId == _uiThread) action();
        else App.Active?.Post(action);
    }

    /// <summary>
    ///     Render a short test clip once and reuse it. ffmpeg's own generators mean the gallery
    ///     carries no binary asset and still has something with motion and a tone to play.
    /// </summary>
    private static async Task<string> SampleClipAsync()
    {
        string path = Path.Combine(path1: Path.GetTempPath(), path2: "zigote-gallery-sample.mp4");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

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
                     "testsrc2=size=960x540:rate=30:duration=15",
                     "-f",
                     "lavfi",
                     "-i",
                     "sine=frequency=330:duration=15",
                     "-c:v",
                     "libx264",
                     "-preset",
                     "veryfast",
                     "-pix_fmt",
                     "yuv420p",
                     "-c:a",
                     "aac",
                     "-shortest",
                     path,
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not render the sample clip: {(await stderr).Trim()}"
            );
        }

        return path;
    }
}
