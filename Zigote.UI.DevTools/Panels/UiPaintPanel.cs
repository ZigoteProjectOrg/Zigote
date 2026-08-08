using Zigote.Core;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     2D paint load over time (2D · UI): how many <c>ZgPaintCommand</c>s the root tree and the overlay
///     layer emitted per frame. The two-series chart makes repaint regressions obvious — a busy root
///     line while nothing on screen changes means the layer-repaint gate is being defeated.
/// </summary>
public sealed class UiPaintPanel : IDevPanel
{
    private static readonly Color Blue = Color.Rgb(10, 132, 255);
    private static readonly Color Purple = Color.Rgb(191, 90, 242);

    private readonly DevChartCard _card;
    private readonly DevKeyValue _overlay = new("Overlays", valueColor: Purple);
    private readonly DevKeyValue _root = new("Root tree", valueColor: Blue);

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tRoot = new();
    private readonly CachedText _tOverlay = new();

    public UiPaintPanel()
    {
        var chart = DevChart.Sparkline();
        AddLine(
            chart,
            DevChartData.UiCommands,
            "root",
            Blue
        );
        AddLine(
            chart,
            DevChartData.OverlayCommands,
            "overlays",
            Purple
        );
        _card = new DevChartCard(
            chart,
            84f,
            60f,
            "Paint commands — 60 s"
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
            DevChartData.Revision,
            DevChartData.Time,
            App.Active?.Theme ?? ThemeData.Dark
        );
        _root.Value = _tRoot.Update($"{DebugStats.UiPaintCommands}");
        _overlay.Value = _tOverlay.Update($"{DebugStats.OverlayPaintCommands}");
    }

    private static void AddLine(Chart chart, TimeSeriesRing ring, string name, Color color)
    {
        var m = LineMark.Of(ring, s => s.Time, s => s.Value);
        m.Name = name;
        m.Color = color;
        m.Interpolation = ChartInterpolation.Step;
        chart.Marks.Add(m);
    }
}