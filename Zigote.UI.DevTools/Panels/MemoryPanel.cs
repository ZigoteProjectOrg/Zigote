using System.Diagnostics;
using Zigote.Core;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

using Zigote.UI.Host;
namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     Managed + process memory over time: GC heap vs working set, the managed / unmanaged split,
///     allocation rate, and per-generation GC frequency — the numbers that catch a leak or a hot-path
///     allocation regression at a glance. Sampled at 1 Hz into 2-minute rings.
/// </summary>
public sealed class MemoryPanel : IDevPanel
{
    private const double TextRefreshMs = 250.0;

    private static readonly Color Blue = Color.Rgb(10, 132, 255);
    private static readonly Color Green = Color.Rgb(48, 209, 88);
    private static readonly Color Orange = Color.Rgb(255, 159, 10);
    private static readonly Color Red = Color.Rgb(255, 69, 58);
    private static readonly Color Yellow = Color.Rgb(255, 214, 10);

    private readonly DevChartCard _allocCard;
    private readonly DevKeyValue _allocRate = new("Allocations", valueColor: Orange);
    private readonly DevChartCard _gcCard;
    private readonly DevKeyValue _gcCounts = new("Gen 0 / 1 / 2");
    private readonly DevKeyValue _heapInfo = new("Heap");
    private readonly DevMeter _managed = new("Managed heap", Green);
    private readonly DevChartCard _memCard;
    private readonly DevKeyValue _pause = new("GC pause");
    private readonly DevMeter _unmanaged = new("Unmanaged", Blue);
    private long _lastText;

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tManaged = new();
    private readonly CachedText _tUnmanaged = new();
    private readonly CachedText _tAlloc = new();
    private readonly CachedText _tGcCounts = new();
    private readonly CachedText _tPause = new();
    private readonly CachedText _tHeapInfo = new();

    public MemoryPanel()
    {
        var mem = DevChart.Sparkline();
        AddArea(mem, DevChartData.WorkingSetMb, "working set", Blue, 0.15f);
        AddArea(mem, DevChartData.GcHeapMb, "GC heap", Green, 0.25f);
        _memCard = new DevChartCard(mem, 84f, 120f, "Heap vs process — 2 min");

        var alloc = DevChart.Sparkline();
        AddArea(alloc, DevChartData.AllocMbPerSec, "alloc MB/s", Orange, 0.3f);
        _allocCard = new DevChartCard(alloc, 60f, 120f, "Allocation rate — MB/s");

        var gc = DevChart.Sparkline();
        AddLine(gc, DevChartData.Gen0PerSec, "gen0 /s", Yellow);
        AddLine(gc, DevChartData.Gen1PerSec, "gen1 /s", Orange);
        AddLine(gc, DevChartData.Gen2PerSec, "gen2 /s", Red);
        _gcCard = new DevChartCard(gc, 60f, 120f, "GC collections /s");
    }

    public string Title => "Memory";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min) {
            Children = {
                _memCard,
                new DevSectionHeader("Managed vs unmanaged"),
                _managed, _unmanaged, _heapInfo,
                new DevSectionHeader("Allocation rate"),
                _allocCard, _allocRate,
                new DevSectionHeader("GC collections"),
                _gcCard, _gcCounts, _pause,
                new SizedBox(height: Spacing.Sm),
                new Button("Force GC (gen 2)", () => GC.Collect(2, GCCollectionMode.Forced, true))
                    { Style = ButtonStyle.Outlined },
            },
        };
    }

    public void Refresh(float dt)
    {
        var rev = DevChartData.Revision;
        var now = DevChartData.Time;
        var t = App.Active?.Theme ?? ThemeData.Dark;
        _memCard.Sync(rev, now, t);
        _allocCard.Sync(rev, now, t);
        _gcCard.Sync(rev, now, t);

        var ws = DebugStats.MemMb;
        var heap = DebugStats.GcMb;
        var unmanaged = MathF.Max(0f, ws - heap);
        var total = MathF.Max(1f, ws);
        _managed.Value = _tManaged.Update($"{heap:F1} MB");
        _managed.Fraction = heap / total;
        _unmanaged.Value = _tUnmanaged.Update($"{unmanaged:F0} MB  (native · runtime · GPU)");
        _unmanaged.Fraction = unmanaged / total;

        var alloc = DevChartData.AllocMbPerSec.Latest.Value;
        _allocRate.Value = _tAlloc.Update($"{alloc:F2} MB/s");
        _allocRate.ValueColor = alloc > 8f ? Orange : t.OnSurface;

        _gcCounts.Value = _tGcCounts.Update(
            $"{DebugStats.Gen0Collections} / {DebugStats.Gen1Collections} / {DebugStats.Gen2Collections}");

        RefreshText(t);
    }

    private void RefreshText(ThemeData t)
    {
        var nowTs = Stopwatch.GetTimestamp();
        if ((nowTs - _lastText) * 1000.0 / Stopwatch.Frequency < TextRefreshMs) return;
        _lastText = nowTs;
        try
        {
            var info = GC.GetGCMemoryInfo();
            _pause.Value = _tPause.Update($"{info.PauseTimePercentage:F2} % of time");
            _pause.ValueColor = t.Hint;
            var frag = info.FragmentedBytes / (1024.0 * 1024.0);
            _heapInfo.Value = _tHeapInfo.Update(
                $"committed {info.TotalCommittedBytes / (1024.0 * 1024.0):F0} MB · frag {frag:F1} MB");
            _heapInfo.ValueColor = t.Hint;
        }
        catch
        {
            _pause.Value = _heapInfo.Value = "—";
        }
    }

    private static void AddArea(Chart chart, TimeSeriesRing ring, string name, Color color, float op)
    {
        var m = AreaMark.Of(ring, s => s.Time, s => s.Value);
        m.Name = name;
        m.Color = color;
        m.Opacity = op;
        chart.Marks.Add(m);
    }

    private static void AddLine(Chart chart, TimeSeriesRing ring, string name, Color color)
    {
        var m = LineMark.Of(ring, s => s.Time, s => s.Value);
        m.Name = name;
        m.Color = color;
        m.Interpolation = ChartInterpolation.Step;
        m.StrokeWidth = 1.5f;
        chart.Marks.Add(m);
    }
}
