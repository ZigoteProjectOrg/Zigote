using Zigote.Core;
using Zigote.Core.Engine;
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
///     Rendering-pipeline load over time (3D · Render): draw calls, triangles, visible objects after
///     culling, and render-pass count on 60 s rolling charts. The "what is the frame actually doing"
///     counters a graphics programmer watches while optimizing. Honest about the native quirk that
///     UI-only frames freeze the counters — the panel flags stale counters rather than implying a
///     live zero-cost scene.
/// </summary>
public sealed class PipelinePanel : IDevPanel
{
    private static readonly Color Blue = Color.Rgb(10, 132, 255);
    private static readonly Color Cyan = Color.Rgb(100, 210, 255);
    private static readonly Color Green = Color.Rgb(48, 209, 88);
    private static readonly Color Purple = Color.Rgb(191, 90, 242);

    private readonly DevChartCard _draws;
    private readonly DevKeyValue _drawsNow = new("Current", valueColor: Blue);
    private readonly DevKeyValue _frameIndex = new("Frame index");
    private readonly DevNote _idle = new("");
    private readonly DevChartCard _passes;
    private readonly DevKeyValue _passesNow = new("Per frame", valueColor: Purple);
    private readonly DevChartCard _tris;
    private readonly DevKeyValue _trisNow = new("Current", valueColor: Cyan);
    private readonly DevChartCard _visible;
    private readonly DevKeyValue _visibleNow = new("After culling", valueColor: Green);

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tDraws = new();
    private readonly CachedText _tVisible = new();
    private readonly CachedText _tPasses = new();
    private readonly CachedText _tFrame = new();
    private long _trisKey = -1;
    private string _trisText = "—";

    public PipelinePanel()
    {
        _draws = Card(DevChartData.DrawCalls, "draw calls", Blue);
        _tris = Card(DevChartData.Triangles, "triangles", Cyan);
        _visible = Card(DevChartData.VisibleObjects, "visible", Green);
        _passes = Card(DevChartData.RenderPasses, "passes", Purple);
    }

    public string Title => "Pipeline";
    public DevCategory Category => DevCategory.Render3D;
    public bool IsAvailable => ZigoteEngine.Instance is not null;

    public Widget Build(BuildContext context)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                _idle,
                new DevSectionHeader("Draw calls"),
                _draws,
                _drawsNow,
                new DevSectionHeader("Triangles"),
                _tris,
                _trisNow,
                new DevSectionHeader("Visible objects"),
                _visible,
                _visibleNow,
                new DevSectionHeader("Render passes"),
                _passes,
                _passesNow,
                _frameIndex,
            },
        };
    }

    public void Refresh(float dt)
    {
        var t = App.Active?.Theme ?? ThemeData.Dark;
        var rev = DevChartData.Revision;
        var now = DevChartData.Time;
        _draws.Sync(rev, now, t);
        _tris.Sync(rev, now, t);
        _visible.Sync(rev, now, t);
        _passes.Sync(rev, now, t);

        if (!DebugStats.EngineOk)
        {
            _idle.Text = "Renderer stats unavailable on this backend.";
            _idle.Color = t.Hint;
            return;
        }

        var s = DebugStats.Engine;
        _idle.Text = DevChartData.Rendering3D
            ? ""
            : "3D idle — counters frozen at the last rendered frame";
        _idle.Color = Color.Amber;
        _drawsNow.Value = _tDraws.Update($"{s.DrawCalls}");
        // DevFormat.Count allocates, so re-run it only when the count changed.
        long trisNow = s.Triangles;
        if (trisNow != _trisKey)
        {
            _trisKey = trisNow;
            _trisText = DevFormat.Count(trisNow);
        }

        _trisNow.Value = _trisText;
        _visibleNow.Value = _tVisible.Update($"{s.VisibleObjects}");
        _passesNow.Value = _tPasses.Update($"{s.RenderPasses}");
        _frameIndex.Value = _tFrame.Update($"{s.FrameIndex}");
        _frameIndex.ValueColor = t.Hint;
    }

    private static DevChartCard Card(TimeSeriesRing ring, string name, Color color)
    {
        var chart = DevChart.Sparkline();
        var m = AreaMark.Of(ring, s => s.Time, s => s.Value);
        m.Name = name;
        m.Color = color;
        m.Opacity = 0.22f;
        m.Interpolation = ChartInterpolation.Step;
        chart.Marks.Add(m);
        return new DevChartCard(chart, 56f, 60f);
    }
}