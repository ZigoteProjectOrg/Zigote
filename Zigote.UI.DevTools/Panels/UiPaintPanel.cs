using Zigote.Core;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     2D paint load over time (2D · UI): how many <c>ZgPaintCommand</c>s the root tree and the
///     overlay
///     layer emitted per frame. The two-series chart makes repaint regressions obvious — a busy root
///     line while nothing on screen changes means the layer-repaint gate is being defeated.
/// </summary>
public sealed class UiPaintPanel : IDevPanel
{
    private static readonly Color Blue = Color.Rgb(r: 10, g: 132, b: 255);
    private static readonly Color Purple = Color.Rgb(r: 191, g: 90, b: 242);

    private readonly DevChartCard _card;
    private readonly DevKeyValue _overlay = new(key: "Overlays", valueColor: Purple);
    private readonly DevKeyValue _root = new(key: "Root tree", valueColor: Blue);
    private readonly CachedText _tOverlay = new();

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tRoot = new();

    public UiPaintPanel()
    {
        var chart = DevChart.Sparkline();
        AddLine(
            chart: chart,
            ring: DevChartData.UiCommands,
            name: "root",
            color: Blue
        );
        AddLine(
            chart: chart,
            ring: DevChartData.OverlayCommands,
            name: "overlays",
            color: Purple
        );
        _card = new DevChartCard(
            chart: chart,
            height: 84f,
            windowSeconds: 60f,
            title: "Paint commands — 60 s"
        );
    }

    public string Title => "UI Paint";
    public DevCategory Category => DevCategory.Ui2D;

    public Widget Build(BuildContext context)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new DevSectionHeader("Paint commands"),
                _card,
                _root,
                _overlay,
                new DevNote("A flat root line while idle means the repaint gate is holding."),
            },
        };
    }

    public void Refresh(float dt)
    {
        _card.Sync(
            revision: DevChartData.Revision,
            now: DevChartData.Time,
            theme: App.Active?.Theme ?? ThemeData.Dark
        );
        _root.Value = _tRoot.Update($"{DebugStats.UiPaintCommands}");
        _overlay.Value = _tOverlay.Update($"{DebugStats.OverlayPaintCommands}");
    }

    private static void AddLine(Chart chart, TimeSeriesRing ring, string name, Color color)
    {
        var m = LineMark.Of(data: ring, x: s => s.Time, y: s => s.Value);
        m.Name = name;
        m.Color = color;
        m.Interpolation = ChartInterpolation.Step;
        chart.Marks.Add(m);
    }
}
