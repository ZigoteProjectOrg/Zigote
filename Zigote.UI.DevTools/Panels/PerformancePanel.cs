using System.Diagnostics;
using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.UI.Charts.Marks;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     CPU profiler: a rolling frame-time chart with the 60/30 fps budget lines, the min/avg/max frame
///     stats, and a live "hottest scopes" table (per-scope self vs total time from the last frame's
///     <see cref="Profiler" /> events, refreshed at ~10 Hz). A Chrome-Trace capture button dumps 120
///     frames for chrome://tracing / Perfetto.
/// </summary>
public sealed class PerformancePanel : IDevPanel
{
    private const double ScopeRefreshMs = 100.0;

    private static readonly Color Blue = Color.Rgb(10, 132, 255);
    private static readonly Color Green = Color.Rgb(48, 209, 88);
    private static readonly Color Orange = Color.Rgb(255, 159, 10);

    private readonly DevChartCard _frameCard;
    private readonly DevKeyValue _stats = new("Avg / min / max");

    private readonly Column _scopeList = new(
        crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min
    );

    private readonly List<DebugProfiler.ScopeAggregate> _agg = [];
    private long _lastScope;

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tStats = new();

    public PerformancePanel()
    {
        var frame = DevChart.Sparkline();
        var area = AreaMark.Of(DevChartData.FrameMs, s => s.Time, s => s.Value);
        area.Name = "frame ms";
        area.Color = Blue;
        area.Opacity = 0.25f;
        frame.Marks.Add(area);
        frame.Marks.Add(
            new RuleMark {
                Y = 1000.0 / 60.0,
                Label = "60",
                Color = Green.WithAlpha(0.55f),
                Dash = 4f,
            }
        );
        frame.Marks.Add(
            new RuleMark {
                Y = 1000.0 / 30.0,
                Label = "30",
                Color = Orange.WithAlpha(0.55f),
                Dash = 4f,
            }
        );
        _frameCard = new DevChartCard(
            frame,
            80f,
            60f,
            "Frame time (ms) — 60 s"
        );
    }

    public string Title => "Profiler";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                _frameCard,
                _stats,
                new SizedBox(height: Spacing.Xs),
                new Button(
                    "Capture 120 frames → profile_capture.json",
                    () => Profiler.Capture(120, "profile_capture.json")
                ) { Style = ButtonStyle.Outlined },
                new DevSectionHeader("Hottest scopes (self · total)"),
                _scopeList,
            },
        };
    }

    public void Refresh(float dt)
    {
        var t = App.Active?.Theme ?? ThemeData.Dark;
        _frameCard.Sync(DevChartData.Revision, DevChartData.Time, t);

        var (min, max, avg) = DebugProfiler.Stats();
        _stats.Value = _tStats.Update($"{avg:F2} / {min:F2} / {max:F2} ms");
        _stats.ValueColor = max > 1000.0 / 30.0 ? Orange : t.OnSurface;

        var nowTs = Stopwatch.GetTimestamp();
        if ((nowTs - _lastScope) * 1000.0 / Stopwatch.Frequency < ScopeRefreshMs) return;
        _lastScope = nowTs;

        _agg.Clear();
        _agg.AddRange(DebugProfiler.Aggregate(Profiler.LastFrame));
        var rows = new List<Widget>();
        var shown = Math.Min(_agg.Count, 16);
        for (var i = 0; i < shown; i++)
        {
            var a = _agg[i];
            var name = a.Calls > 1 ? $"{a.Name} ×{a.Calls}" : a.Name;
            rows.Add(new DevKeyValue(name, $"{a.SelfMs:F2} · {a.TotalMs:F2}"));
        }

        if (rows.Count == 0)
            rows.Add(new DevNote("No profiled scopes in the last frame."));
        _scopeList.SetChildren(rows);
    }
}