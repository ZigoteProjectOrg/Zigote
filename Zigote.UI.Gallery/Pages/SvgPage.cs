using System.Diagnostics;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.Http;
using Zigote.Http.Cache;
using Zigote.UI.Svg;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Transitions;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     SVG through <see cref="SvgPicture" />: a document whose size animates — re-rasterized by
///     resvg at every size it passes through, which is the whole reason to ship vector art — a
///     scrolling strip of real-world samples, one parsed document tinted several ways, and a
///     measured look at what compiling an SVG ahead of time actually buys.
///     <para>
///         Nothing is committed to the repo: the samples come from the W3C's SVG Web sample set
///         over https, through a page-owned <see cref="HttpRunner" /> with a disk cache, so the
///         second visit is served from disk (the origin sends validators; heuristic freshness is
///         turned on because these files haven't changed in a decade). Offline, the page says so
///         and stays empty rather than pretending to load.
///     </para>
/// </summary>
internal sealed class SvgPage : ComposedWidget
{
    private const string BaseUrl = "https://dev.w3.org/SVG/tools/svgweb/samples/svg-files/";

    /// <summary>
    ///     One runner for the page's few dozen small files. Heuristic freshness is the point here:
    ///     the W3C serves these with a Last-Modified from another era and no max-age, so the RFC's
    ///     10%-of-age rule (capped at a day) is what lets the second visit skip the network
    ///     entirely instead of revalidating every file.
    /// </summary>
    private static readonly HttpRunner Http = new(new HttpRunnerOptions {
        BaseAddress = new Uri(BaseUrl),
        Cache = new FileCacheStore(FileCacheStore.DefaultDirectory),
        DefaultPolicy = RequestPolicy.Default with { AllowHeuristicFreshness = true },
        MaxConcurrencyPerHost = 6,
    });

    /// <summary>The hero: ~240 paths of it, the classic stress test for a rasterizer.</summary>
    private const string Hero = "tiger";

    /// <summary>Text- and CSS-heavy, so compiling it has something to remove. Never displayed.</summary>
    private const string TextHeavy = "Steps";

    private const float SmallSize = 96;
    private const float LargeSize = 260;

    private static readonly string[] SampleNames = [
        "android", "debian", "ubuntu", "python", "ruby", "opera", "twitter", "w3c", "yinyang",
        "star", "smile", "compass", "duck", "penrose-tiling", "radialgradient1", "alphachannel",
    ];

    private readonly Signal<string> _compiled = new("Measuring…");
    private readonly CancellationTokenSource _cts = new();
    private readonly Signal<SvgAsset?> _hero = new(null);

    /// <summary>
    ///     Every texture-owning widget this page made. Nothing else frees them: an
    ///     <see cref="SvgPicture" />'s texture goes when it is disposed, so the page keeps the list
    ///     and empties it on unmount.
    /// </summary>
    private readonly List<IDisposable> _owned = [];

    private readonly Signal<bool> _playing = new(true);

    private readonly Dictionary<string, Signal<SvgAsset?>> _samples =
        SampleNames.ToDictionary(keySelector: n => n, elementSelector: _ => new Signal<SvgAsset?>(null));

    private readonly Signal<string> _status = new("Fetching samples from dev.w3.org…");

    private bool _big;
    private AnimatedContainer? _heroBox;
    private Widget? _root;

    protected override void OnMount() => _ = LoadAsync(_cts.Token);

    protected override void OnUnmount()
    {
        _cts.Cancel();
        foreach (var owned in _owned) owned.Dispose();
        _owned.Clear();

        // The pictures went first — a picture drawn from a disposed document would be a use after
        // free, and the page owns both.
        _hero.Value?.Dispose();
        foreach (var sample in _samples.Values) sample.Value?.Dispose();
    }

    protected override Widget Build(BuildContext context)
    {
        if (_root is not null) return _root;

        _heroBox = new AnimatedContainer(
            width: SmallSize,
            height: SmallSize,
            color: Colors.Transparent,
            durationSeconds: 1.6f,
            child: new Watch(() => _hero.Value is { } asset
                ? Own(new SvgPicture(asset) { AltText = "The SVG tiger" })
                : new Center(child: new Text("…"))
            )
        );
        // Ping-pong: each leg ends by aiming at the other size, so the picture is re-rasterized at
        // every intermediate size — 60 fresh rasters a second, which is the demo.
        _heroBox.Controller.OnCompleted += () =>
        {
            if (_playing.Value) ToggleSize();
        };

        return _root = Sections(
            Section(
                title: "Rasterized at every size it passes through",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new SizedBox(height: LargeSize, child: new Center(child: _heroBox)),
                        new SizedBox(height: 12),
                        new Row(
                            mainAxisSize: MainAxisSize.Min,
                            children: [
                                new Watch(() => new FilledButton(
                                        child: new Text(_playing.Value ? "Pause" : "Play"),
                                        onPressed: TogglePlaying
                                    )
                                ),
                                new SizedBox(12),
                                new OutlinedButton(
                                    child: new Text("Toggle size"),
                                    onPressed: () =>
                                    {
                                        _playing.Value = false;
                                        ToggleSize();
                                    }
                                ),
                            ]
                        ),
                        new SizedBox(height: 12),
                        Caption(
                            $"An AnimatedContainer eases between {SmallSize:0} and {LargeSize:0} px. " +
                            "SvgPicture re-rasterizes whenever its pixel size changes, so the edges " +
                            "stay exact at every frame — a PNG scaled the same way would soften. " +
                            "The cost is real: this document is a few milliseconds per raster, so a " +
                            "continuously animated illustration is not free. Icons are microseconds."
                        ),
                    ]
                )
            ),
            Section(
                title: "One parsed document, many tints",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new Watch(() => _samples["star"].Value is { } star
                            ? new Wrap(
                                spacing: 16,
                                runSpacing: 12,
                                children: [
                                    Tinted(star, null), Tinted(star, Colors.Red),
                                    Tinted(star, Colors.Teal), Tinted(star, Colors.Indigo),
                                    Tinted(star, Colors.Amber),
                                ]
                            )
                            : new Text("…")
                        ),
                        new SizedBox(height: 12),
                        Caption(
                            "One SvgAsset behind five pictures — parsing is the expensive half, so an " +
                            "icon that appears on every row is parsed once and drawn many times. " +
                            "ColorFilter tints at paint time (flutter_svg's colorFilter); no reparse, " +
                            "no second texture."
                        ),
                    ]
                )
            ),
            Section(
                title: "Samples — scroll sideways",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new SizedBox(
                            height: 124,
                            child: new SingleChildScrollView(
                                scrollDirection: Axis.Horizontal,
                                child: new Row(
                                    mainAxisSize: MainAxisSize.Min,
                                    children: [.. SampleNames.Select(Tile)]
                                )
                            )
                        ),
                        new SizedBox(height: 8),
                        new Watch(() => Caption(_status.Value)),
                    ]
                )
            ),
            Section(
                title: "Compiled ahead of time",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        new Watch(() => new Text(
                                data: _compiled.Value,
                                style: new TextStyle(fontSize: 13, fontFamily: "Iosevka")
                            )
                        ),
                        new SizedBox(height: 12),
                        Caption(
                            "`zigote-svgc in.svg out.svg` resolves the CSS, the text and the " +
                            "references ahead of time and writes the document back out as (still) " +
                            "SVG, so the same loader takes it. The win is proportional to what there " +
                            "was to resolve: a text- and stylesheet-heavy document parses several " +
                            "times faster, plain path art is a wash and gets bigger. Text is the one " +
                            "that always pays — a compiled document has none, so the font database " +
                            "is never enumerated."
                        ),
                    ]
                )
            )
        );
    }

    private static Widget Caption(string text) => new Text(
        data: text,
        style: new TextStyle(fontSize: 12, color: Colors.Gray)
    );

    private Widget Tinted(SvgAsset asset, Color? tint) =>
        Own(new SvgPicture(asset) { Height = 44, ColorFilter = tint, AltText = "Star" });

    /// <summary>Track a picture so its texture is released when the page goes.</summary>
    private SvgPicture Own(SvgPicture picture)
    {
        _owned.Add(picture);
        return picture;
    }

    private Widget Tile(string name)
    {
        return new Padding(
            padding: EdgeInsets.Only(right: 12),
            child: new SizedBox(
                width: 96,
                child: new Column(
                    children: [
                        new SizedBox(
                            height: 84,
                            child: new Center(
                                child: new Watch(() => _samples[name].Value is { } asset
                                    ? Own(new SvgPicture(asset) { Height = 72, AltText = name })
                                    : new Text("…", style: new TextStyle(color: Colors.Gray))
                                )
                            )
                        ),
                        new Text(
                            data: name,
                            style: new TextStyle(fontSize: 11, color: Colors.Gray)
                        ),
                    ]
                )
            )
        );
    }

    private void TogglePlaying()
    {
        _playing.Value = !_playing.Value;
        if (_playing.Value) ToggleSize();
    }

    private void ToggleSize()
    {
        _big = !_big;
        float size = _big ? LargeSize : SmallSize;
        _heroBox?.AnimateTo(width: size, height: size);
    }

    /// <summary>
    ///     Fetch and parse everything off the UI thread. Signals are marshalled by
    ///     <see cref="Watch" />, so each picture appears the frame after its bytes land — no
    ///     App.Post here.
    /// </summary>
    private async Task LoadAsync(CancellationToken token)
    {
        var hero = await ParseAsync(name: Hero, token: token).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            hero?.Dispose();
            return;
        }

        _hero.Value = hero;
        if (hero is not null && _playing.Value) App.Active?.Post(ToggleSize);
        if (hero is null) _status.Value = $"Could not fetch {Hero}.svg — offline?";

        var failed = new List<string>();
        foreach (string name in SampleNames)
        {
            var asset = await ParseAsync(name: name, token: token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                asset?.Dispose();
                return;
            }

            if (asset is null) failed.Add(name);
            else _samples[name].Value = asset;
        }

        _status.Value = failed.Count == 0
            ? $"{SampleNames.Length} samples from dev.w3.org/SVG/tools/svgweb, cached on disk after the first visit."
            : $"{failed.Count} of {SampleNames.Length} could not be fetched ({string.Join(", ", failed)}).";

        await MeasureCompiledAsync(token).ConfigureAwait(false);
    }

    private static async Task<SvgAsset?> ParseAsync(string name, CancellationToken token)
    {
        var bytes = await Http.BytesAsync(HttpRequest.Get(name + ".svg"), token).ConfigureAwait(false);
        if (!bytes.IsOk) return null; // offline or a 404 — the same to the page: no picture

        try
        {
            return SvgAsset.FromBytes(bytes.Value);
        }
        catch (Exception)
        {
            // Bytes that are not an SVG.
            return null;
        }
    }

    /// <summary>
    ///     Parse each document ten times authored and ten times compiled, and report the byte
    ///     sizes with it. Ten because a single parse of a small document is under the clock's
    ///     resolution; the first of each is included on purpose — it is what an app pays.
    /// </summary>
    private async Task MeasureCompiledAsync(CancellationToken token)
    {
        try
        {
            var rows = new List<string>();
            foreach (string name in (string[]) [Hero, TextHeavy])
            {
                // Unwrap: this path already reports any failure as one line via the catch below.
                byte[] raw = (await Http.BytesAsync(HttpRequest.Get(name + ".svg"), token)
                    .ConfigureAwait(false)).Unwrap();
                byte[] compiled = SvgAsset.Compile(raw);

                rows.Add(
                    $"{name + ".svg",-14} authored {raw.Length / 1024f,6:0.0} kB  {ParseMs(raw),5:0.00} ms" +
                    $"   compiled {compiled.Length / 1024f,6:0.0} kB  {ParseMs(compiled),5:0.00} ms"
                );
            }

            _compiled.Value = string.Join(separator: "\n", values: rows);
        }
        catch (Exception e)
        {
            _compiled.Value = $"Could not measure: {e.Message}";
        }
    }

    private static double ParseMs(byte[] svg)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) SvgAsset.FromBytes(svg).Dispose();
        return sw.Elapsed.TotalMilliseconds / 10;
    }
}
