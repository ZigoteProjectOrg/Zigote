using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Rendering;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Charts.Scales;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     "Health at a glance": rolling FPS with 60/30 budget rules, CPU load, and managed-vs-process
///     memory — each an interactive chart with hover readout — plus the renderer counters and log
///     health. The canonical example of a widget/chart devtools panel: build the retained tree once in
///     <see cref="Build" />, mutate the retained labels + sync the charts in <see cref="Refresh" />.
/// </summary>
public sealed class OverviewPanel : IDevPanel
{
    private static readonly Color Blue = Color.Rgb(r: 10, g: 132, b: 255);
    private static readonly Color Green = Color.Rgb(r: 48, g: 209, b: 88);
    private static readonly Color Orange = Color.Rgb(r: 255, g: 159, b: 10);

    private readonly DevKeyValue _backend = new("Backend");
    private readonly DevKeyValue _cpu = new(key: "Load", valueColor: Blue);
    private readonly DevChartCard _cpuCard;
    private readonly DevKeyValue _draws = new("Draw calls");
    private readonly DevKeyValue _errors = new("Errors");
    private readonly DevKeyValue _fps = new("FPS");
    private readonly DevChartCard _fpsCard;
    private readonly DevKeyValue _frame = new("Frame time");
    private readonly DevKeyValue _heap = new(key: "GC heap", valueColor: Green);
    private readonly DevNote _idle = new("3D idle — counters show the last rendered frame");
    private readonly DevKeyValue _info = new("Info");
    private readonly DevChartCard _memCard;
    private readonly DevKeyValue _range = new("Range");
    private readonly DevKeyValue _jank = new("Jank");
    private readonly DevKeyValue _surface = new("Surface");
    private readonly CachedText _tCpu = new();
    private readonly CachedText _tDraws = new();
    private readonly CachedText _tErrors = new();

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tFps = new();
    private readonly CachedText _tFrame = new();
    private readonly CachedText _tHeap = new();
    private readonly CachedText _tInfo = new();
    private readonly CachedText _tRange = new();
    private readonly CachedText _tJank = new();
    private readonly CachedText _tSurface = new();
    private readonly CachedText _tUptime = new();
    private readonly CachedText _tVisible = new();
    private readonly CachedText _tWarnings = new();
    private readonly CachedText _tWs = new();
    private readonly DevKeyValue _tris = new("Triangles");
    private readonly DevKeyValue _uptime = new("Uptime");
    private readonly DevKeyValue _visible = new("Visible objects");
    private readonly DevKeyValue _warnings = new("Warnings");
    private readonly DevKeyValue _ws = new(key: "Working set", valueColor: Blue);
    private RenderBackend? _backendKey;
    private string _backendText = "—";
    private long _trisKey = -1;
    private string _trisText = "—";

    public OverviewPanel()
    {
        var fps = DevChart.Sparkline();
        var fpsArea = AreaMark.Of(data: DevChartData.Fps, x: s => s.Time, y: s => s.Value);
        fpsArea.Name = "fps";
        fpsArea.Color = Green;
        fpsArea.Opacity = 0.2f;
        fpsArea.Interpolation = ChartInterpolation.Linear;
        fps.Marks.Add(fpsArea);
        fps.Marks.Add(
            // No labels: the y-axis already numbers the scale, and a rule label lands on top of it.
            new RuleMark {
                Y = 60,
                Color = Green.WithAlpha(0.55f),
                Dash = 4f,
            }
        );
        fps.Marks.Add(
            new RuleMark {
                Y = 30,
                Color = Orange.WithAlpha(0.55f),
                Dash = 4f,
            }
        );
        _fpsCard = new DevChartCard(chart: fps, height: 78f, windowSeconds: 60f);

        var cpu = DevChart.Sparkline();
        cpu.YScale = new LinearScale {
            Min = 0,
            Max = 100,
            Nice = false,
        };
        var cpuArea = AreaMark.Of(data: DevChartData.CpuPct, x: s => s.Time, y: s => s.Value);
        cpuArea.Name = "cpu %";
        cpuArea.Color = Blue;
        cpuArea.Opacity = 0.18f;
        cpuArea.Interpolation = ChartInterpolation.Monotone;
        cpu.Marks.Add(cpuArea);
        _cpuCard = new DevChartCard(chart: cpu, height: 54f, windowSeconds: 120f);

        var mem = DevChart.Sparkline();
        var ws = LineMark.Of(data: DevChartData.WorkingSetMb, x: s => s.Time, y: s => s.Value);
        ws.Name = "working set";
        ws.Color = Blue;
        var heap = LineMark.Of(data: DevChartData.GcHeapMb, x: s => s.Time, y: s => s.Value);
        heap.Name = "GC heap";
        heap.Color = Green;
        mem.Marks.Add(ws);
        mem.Marks.Add(heap);
        _memCard = new DevChartCard(chart: mem, height: 62f, windowSeconds: 120f);
    }

    public string Title => "Overview";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new DevSectionHeader("Frame"),
                _fpsCard,
                _fps,
                _frame,
                _range,
                _jank,
                new DevSectionHeader("CPU"),
                _cpuCard,
                _cpu,
                new DevSectionHeader("Memory"),
                _memCard,
                _ws,
                _heap,
                new DevSectionHeader("Renderer"),
                _backend,
                _surface,
                _draws,
                _tris,
                _visible,
                _idle,
                new DevSectionHeader("Health"),
                _errors,
                _warnings,
                _info,
                _uptime,
            },
        };
    }

    public void Refresh(float dt)
    {
        int rev = DevChartData.Revision;
        float now = DevChartData.Time;
        var theme = App.Active?.Theme ?? ThemeData.Dark;
        _fpsCard.Sync(revision: rev, now: now, theme: theme);
        _cpuCard.Sync(revision: rev, now: now, theme: theme);
        _memCard.Sync(revision: rev, now: now, theme: theme);

        float fps = DebugStats.Fps;
        _fps.Value = _tFps.Update($"{fps:F0}");
        _fps.ValueColor = fps >= 55f ? Color.Green : fps >= 30f ? Color.Amber : Color.Red;
        _frame.Value = _tFrame.Update($"{DebugStats.FrameMs:F2} ms");
        _range.Value = _tRange.Update($"{DebugStats.FpsMin:F0} – {DebugStats.FpsMax:F0} fps");
        _range.ValueColor = theme.Hint;

        // Missed-deadline frames since launch; the parenthesized count is the subset that hit
        // while something was animating (scroll/fling/transition) — the jank users actually see.
        // The trailing name is the pipeline stage most often to blame (profiler-bracketed hosts).
        long total = Math.Max(val1: 1L, val2: DebugStats.TotalFrames);
        string worst = "";
        long worstCount = 0;
        foreach ((string name, long count) in DebugStats.JankCauses)
        {
            if (count <= worstCount) continue;
            worstCount = count;
            worst = $" — {name}";
        }

        _jank.Value = _tJank.Update(
            $"{DebugStats.JankFrames} ({DebugStats.AnimatedJankFrames} animated, {DebugStats.JankFrames * 100.0 / total:F1}%){worst}"
        );
        _jank.ValueColor = DebugStats.AnimatedJankFrames > 0 ? Color.Amber : theme.Hint;

        _cpu.Value = _tCpu.Update($"{DebugStats.CpuPct:F1}%  ({Environment.ProcessorCount} cores)");
        _ws.Value = _tWs.Update($"{DebugStats.MemMb:0.0} MB");
        _heap.Value = _tHeap.Update($"{DebugStats.GcMb:0.0} MB");

        var engine = ZigoteEngine.Instance;
        // Enum.ToString allocates each call, so re-run it only when the backend changed.
        var backendNow = engine?.Caps.ActiveBackend;
        if (backendNow != _backendKey)
        {
            _backendKey = backendNow;
            _backendText = backendNow?.ToString() ?? "—";
        }

        _backend.Value = _backendText;
        _surface.Value = engine is not null
            ? _tSurface.Update(
                $"{engine.LogicalWidth:F0}×{engine.LogicalHeight:F0} @{engine.Scale:0.#}x"
            )
            : "—";

        if (DebugStats.EngineOk)
        {
            var s = DebugStats.Engine;
            _draws.Value = _tDraws.Update($"{s.DrawCalls}");
            // DevFormat.Count allocates, so re-run it only when the count changed.
            long trisNow = s.Triangles;
            if (trisNow != _trisKey)
            {
                _trisKey = trisNow;
                _trisText = DevFormat.Count(trisNow);
            }

            _tris.Value = _trisText;
            _visible.Value = _tVisible.Update($"{s.VisibleObjects}");
            _idle.Text = DevChartData.Rendering3D
                ? ""
                : "3D idle — counters show the last rendered frame";
            _idle.Color = theme.Hint;
        }
        else
        {
            _draws.Value = _tris.Value = _visible.Value = "n/a";
            _idle.Text = "";
        }

        (_, _, int info, int warn, int err, int fatal) = DebugLog.Counts();
        _errors.Value = _tErrors.Update($"{err + fatal}");
        _errors.ValueColor = err + fatal > 0 ? theme.Error : theme.Hint;
        _warnings.Value = _tWarnings.Update($"{warn}");
        _warnings.ValueColor = warn > 0 ? Color.Amber : theme.Hint;
        _info.Value = _tInfo.Update($"{info}");
        _info.ValueColor = theme.Hint;
        var up = TimeSpan.FromSeconds(Math.Max(val1: 0f, val2: App.Active?.Time ?? 0f));
        _uptime.Value = up.TotalHours >= 1
            ? _tUptime.Update($"{(int)up.TotalHours}h {up.Minutes}m {up.Seconds}s")
            : up.TotalMinutes >= 1
                ? _tUptime.Update($"{up.Minutes}m {up.Seconds}s")
                : _tUptime.Update($"{up.Seconds}s");
    }
}
