using System.Diagnostics;
using Zigote.Core;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     Managed + process memory over time: GC heap vs working set, the managed / unmanaged split,
///     allocation rate, and per-generation GC frequency — the numbers that catch a leak or a hot-path
///     allocation regression at a glance. Sampled at 1 Hz into 2-minute rings.
/// </summary>
public sealed class MemoryPanel : IDevPanel
{
    private const double TextRefreshMs = 250.0;

    private static readonly Color Blue = Color.Rgb(r: 10, g: 132, b: 255);
    private static readonly Color Green = Color.Rgb(r: 48, g: 209, b: 88);
    private static readonly Color Orange = Color.Rgb(r: 255, g: 159, b: 10);
    private static readonly Color Red = Color.Rgb(r: 255, g: 69, b: 58);
    private static readonly Color Yellow = Color.Rgb(r: 255, g: 214, b: 10);

    private readonly DevChartCard _allocCard;
    private readonly DevKeyValue _allocRate = new(key: "Allocations", valueColor: Orange);
    private readonly DevChartCard _gcCard;
    private readonly DevKeyValue _gcCounts = new("Gen 0 / 1 / 2");
    private readonly DevKeyValue _heapInfo = new("Heap");
    private readonly DevMeter _managed = new(key: "Managed heap", color: Green);
    private readonly DevChartCard _memCard;
    private readonly DevKeyValue _pause = new("GC pause");
    private readonly CachedText _tAlloc = new();
    private readonly CachedText _tGcCounts = new();
    private readonly CachedText _tHeapInfo = new();

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tManaged = new();
    private readonly CachedText _tPause = new();
    private readonly CachedText _tUnmanaged = new();
    private readonly DevMeter _unmanaged = new(key: "Unmanaged", color: Blue);
    private long _lastText;

    public MemoryPanel()
    {
        var mem = DevChart.Sparkline();
        AddArea(
            chart: mem,
            ring: DevChartData.WorkingSetMb,
            name: "working set",
            color: Blue,
            op: 0.15f
        );
        AddArea(
            chart: mem,
            ring: DevChartData.GcHeapMb,
            name: "GC heap",
            color: Green,
            op: 0.25f
        );
        _memCard = new DevChartCard(
            chart: mem,
            height: 84f,
            windowSeconds: 120f,
            title: "Heap vs process — 2 min"
        );

        var alloc = DevChart.Sparkline();
        AddArea(
            chart: alloc,
            ring: DevChartData.AllocMbPerSec,
            name: "alloc MB/s",
            color: Orange,
            op: 0.3f
        );
        _allocCard = new DevChartCard(
            chart: alloc,
            height: 60f,
            windowSeconds: 120f,
            title: "Allocation rate — MB/s"
        );

        var gc = DevChart.Sparkline();
        AddLine(
            chart: gc,
            ring: DevChartData.Gen0PerSec,
            name: "gen0 /s",
            color: Yellow
        );
        AddLine(
            chart: gc,
            ring: DevChartData.Gen1PerSec,
            name: "gen1 /s",
            color: Orange
        );
        AddLine(
            chart: gc,
            ring: DevChartData.Gen2PerSec,
            name: "gen2 /s",
            color: Red
        );
        _gcCard = new DevChartCard(
            chart: gc,
            height: 60f,
            windowSeconds: 120f,
            title: "GC collections /s"
        );
    }

    public string Title => "Memory";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                _memCard,
                new DevSectionHeader("Managed vs unmanaged"),
                _managed,
                _unmanaged,
                _heapInfo,
                new DevSectionHeader("Allocation rate"),
                _allocCard,
                _allocRate,
                new DevSectionHeader("GC collections"),
                _gcCard,
                _gcCounts,
                _pause,
                new SizedBox(height: Spacing.Sm),
                new Button(
                    label: "Force GC (gen 2)",
                    onPressed: () => GC.Collect(
                        generation: 2,
                        mode: GCCollectionMode.Forced,
                        blocking: true
                    )
                ) {
                    Style = ButtonStyle.Outlined,
                },
            },
        };
    }

    public void Refresh(float dt)
    {
        int rev = DevChartData.Revision;
        float now = DevChartData.Time;
        var t = App.Active?.Theme ?? ThemeData.Dark;
        _memCard.Sync(revision: rev, now: now, theme: t);
        _allocCard.Sync(revision: rev, now: now, theme: t);
        _gcCard.Sync(revision: rev, now: now, theme: t);

        float ws = DebugStats.MemMb;
        float heap = DebugStats.GcMb;
        float unmanaged = MathF.Max(x: 0f, y: ws - heap);
        float total = MathF.Max(x: 1f, y: ws);
        _managed.Value = _tManaged.Update($"{heap:F1} MB");
        _managed.Fraction = heap / total;
        _unmanaged.Value = _tUnmanaged.Update($"{unmanaged:F0} MB  (native · runtime · GPU)");
        _unmanaged.Fraction = unmanaged / total;

        float alloc = DevChartData.AllocMbPerSec.Latest.Value;
        _allocRate.Value = _tAlloc.Update($"{alloc:F2} MB/s");
        _allocRate.ValueColor = alloc > 8f ? Orange : t.OnSurface;

        _gcCounts.Value = _tGcCounts.Update(
            $"{DebugStats.Gen0Collections} / {DebugStats.Gen1Collections} / {DebugStats.Gen2Collections}"
        );

        RefreshText(t);
    }

    private void RefreshText(ThemeData t)
    {
        long nowTs = Stopwatch.GetTimestamp();
        if ((nowTs - _lastText) * 1000.0 / Stopwatch.Frequency < TextRefreshMs) return;
        _lastText = nowTs;
        try
        {
            var info = GC.GetGCMemoryInfo();
            _pause.Value = _tPause.Update($"{info.PauseTimePercentage:F2} % of time");
            _pause.ValueColor = t.Hint;
            double frag = info.FragmentedBytes / (1024.0 * 1024.0);
            _heapInfo.Value = _tHeapInfo.Update(
                $"committed {info.TotalCommittedBytes / (1024.0 * 1024.0):F0} MB · frag {frag:F1} MB"
            );
            _heapInfo.ValueColor = t.Hint;
        }
        catch
        {
            _pause.Value = _heapInfo.Value = "—";
        }
    }

    private static void AddArea(Chart chart, TimeSeriesRing ring, string name, Color color,
        float op)
    {
        var m = AreaMark.Of(data: ring, x: s => s.Time, y: s => s.Value);
        m.Name = name;
        m.Color = color;
        m.Opacity = op;
        chart.Marks.Add(m);
    }

    private static void AddLine(Chart chart, TimeSeriesRing ring, string name, Color color)
    {
        var m = LineMark.Of(data: ring, x: s => s.Time, y: s => s.Value);
        m.Name = name;
        m.Color = color;
        m.Interpolation = ChartInterpolation.Step;
        m.StrokeWidth = 1.5f;
        chart.Marks.Add(m);
    }
}
