using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.Core.Rendering;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels;

/// <summary>
///     Read-only "information page": runtime + environment details (host runtime/OS, the active GPU
///     backend and its capabilities, live renderer stats, and process memory). Self-painting so the
///     live rows refresh in place without rebuilding a widget tree; live values are sampled at a few
///     hertz whenever the editor repaints (continuously while in play mode).
/// </summary>
public sealed class InfoPanel : Widget
{
    private const float RowH = 19f;
    private const float HeaderH = 22f;
    private const float SectionGap = 8f;
    private const float ValueX = 132f;

    private readonly Process _proc = Process.GetCurrentProcess();

    // Host info is fixed for the process lifetime — computed once.
    private readonly List<(string Section, string Label, string Value)> _static = [];
    private readonly ThemeData _theme;
    private float _gcMb;
    private float _lastLiveT = -10f;
    private float _memMb;
    private Size _size;
    private ZgEngineStats _stats;

    public InfoPanel(ThemeData theme)
    {
        _theme = theme;
        BuildStatic();
    }

    private void BuildStatic()
    {
        _static.Clear();

        string ver = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
        string build =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        _static.Add(("Application", "Editor", $"Zigote {ver}"));
        _static.Add(("Application", "Build", build));

        _static.Add(("Runtime", ".NET", RuntimeInformation.FrameworkDescription));
        _static.Add(("Runtime", "OS", RuntimeInformation.OSDescription));
        _static.Add(
            ("Runtime", "Architecture",
                $"{RuntimeInformation.OSArchitecture} · proc {RuntimeInformation.ProcessArchitecture}")
        );
        _static.Add(("Runtime", "CPU", $"{Environment.ProcessorCount} logical cores"));

        try
        {
            var caps = ZigoteEngine.Instance?.Caps ?? default;
            _static.Add(("Graphics", "Backend", caps.ActiveBackend.ToString()));
            _static.Add(
                ("Graphics", "Ray Tracing",
                    caps.RayTracing
                        ? caps.RayTracingFromRender ? "Yes (+ from render)" : "Yes"
                        : "No")
            );
            var ups = caps.AvailableUpscalers().Where(u => u != UpscalerSelection.Off).ToArray();
            _static.Add(
                ("Graphics", "Upscalers",
                    ups.Length > 0 ? string.Join(separator: ", ", values: ups) : "None")
            );
            _static.Add(("Graphics", "ABI", $"v{RendererAbiInfo.ExpectedAbiVersion}"));
        }
        catch
        {
            _static.Add(("Graphics", "Backend", "unavailable"));
        }
    }

    /// <summary>
    ///     Sample the live counters at most a few times a second (native calls are cheap but not
    ///     free).
    /// </summary>
    private void RefreshLive()
    {
        float now = App.Active?.Time ?? 0f;
        if (now - _lastLiveT < 0.4f) return;
        _lastLiveT = now;

        try
        {
            _proc.Refresh();
            _memMb = _proc.WorkingSet64 / (1024f * 1024f);
        }
        catch
        {
            /* ignore */
        }

        _gcMb = GC.GetTotalMemory(false) / (1024f * 1024f);

        try
        {
            _stats = ZigoteEngine.Instance?.GetEngineStats() ?? default;
        }
        catch
        {
            _stats = default;
        }
    }

    private List<(string Section, string Label, string Value)> BuildRows()
    {
        var rows = new List<(string, string, string)>(_static);

        var engine = ZigoteEngine.Instance;
        if (engine != null)
        {
            try
            {
                rows.Add(
                    ("Graphics", "Surface",
                        $"{engine.LogicalWidth:F0} × {engine.LogicalHeight:F0} @ {engine.Scale:0.##}x")
                );
            }
            catch
            {
                /* not ready */
            }
        }

        float dt = App.Active?.DeltaTime ?? 0f;
        float fps = dt > 0f ? 1f / dt : 0f;
        rows.Add(("Renderer (live)", "FPS", $"{fps:F0}  ({dt * 1000f:F1} ms)"));
        rows.Add(("Renderer (live)", "Draw calls", _stats.DrawCalls.ToString()));
        rows.Add(("Renderer (live)", "Triangles", FormatCount(_stats.Triangles)));
        rows.Add(("Renderer (live)", "Render passes", _stats.RenderPasses.ToString()));
        rows.Add(("Renderer (live)", "Visible objects", _stats.VisibleObjects.ToString()));

        rows.Add(("Process (live)", "Working set", $"{_memMb:F0} MB"));
        rows.Add(("Process (live)", "GC heap", $"{_gcMb:F1} MB"));
        rows.Add(("Process (live)", "Uptime", FormatUptime(App.Active?.Time ?? 0f)));

        return rows;
    }

    private static string FormatCount(uint n)
    {
        return n >= 1_000_000 ? $"{n / 1_000_000.0:F2}M" :
            n >= 1_000 ? $"{n / 1_000.0:F1}K" : n.ToString();
    }

    private static string FormatUptime(float seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
            : ts.TotalMinutes >= 1
                ? $"{ts.Minutes}m {ts.Seconds}s"
                : $"{ts.Seconds}s";
    }

    private float ContentHeight(IReadOnlyList<(string Section, string Label, string Value)> rows)
    {
        float h = 0f;
        string? section = null;
        foreach (var r in rows)
        {
            if (r.Section != section)
            {
                h += SectionGap + HeaderH;
                section = r.Section;
            }

            h += RowH;
        }

        return h + SectionGap;
    }

    public override Size Measure(Constraints c)
    {
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 280f;
        _size = new Size(width: w, height: ContentHeight(BuildRows()));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        RefreshLive();
        var rows = BuildRows();

        float fs = _theme.FontSizeCaption;
        float x = Bounds.X;
        float y = Bounds.Y;
        string? section = null;

        foreach (var r in rows)
        {
            if (r.Section != section)
            {
                y += SectionGap;
                // Accent bar + section title.
                paint.AddRect(
                    bounds: new Rect(
                        x: x,
                        y: y + 4f,
                        width: 3f,
                        height: fs
                    ),
                    color: _theme.Primary,
                    radius: 1.5f
                );
                paint.AddText(
                    text: r.Section,
                    baselineX: x + 8f,
                    baselineY: y + (HeaderH * 0.62f),
                    color: _theme.Primary,
                    fontSize: _theme.FontSizeBody,
                    fontWeight: FontWeight.SemiBold
                );
                y += HeaderH;
                section = r.Section;
            }

            paint.AddText(
                text: r.Label,
                baselineX: x,
                baselineY: y + (RowH * 0.72f),
                color: _theme.Hint,
                fontSize: fs
            );
            paint.AddText(
                text: r.Value,
                baselineX: x + ValueX,
                baselineY: y + (RowH * 0.72f),
                color: _theme.OnSurface,
                fontSize: fs
            );
            y += RowH;
        }
    }
}
