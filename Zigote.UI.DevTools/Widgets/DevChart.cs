using Zigote.Core;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Scales;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>Factory for the compact charts devtools panels use (no axis noise, no animation).</summary>
public static class DevChart
{
    /// <summary>A chart preconfigured as a debug sparkline: linear x/y, hidden legend, quiet axes.</summary>
    public static Chart Sparkline(bool showYAxis = true)
    {
        return new Chart {
            Animated = false,
            AnimateDataUpdates = false,
            LegendPosition = ChartLegendPosition.Hidden,
            XScale = new LinearScale { Nice = false },
            YScale = new LinearScale { Min = 0 },
            XAxis = { Show = false },
            YAxis = { Show = showYAxis, TickTarget = 3 },
        };
    }
}

/// <summary>
///     A titled chart card: an optional caption over a fixed-height <see cref="Chart" />. It owns the
///     rolling x-window bookkeeping — a panel calls <see cref="Sync" /> from
///     <see cref="IDevPanel.Refresh" /> with the current data revision and clock; when the revision moves
///     the card shifts the window and invalidates the chart so it re-resolves scales + geometry from the
///     live rings (a data-only ring push does not otherwise dirty the chart).
/// </summary>
public sealed class DevChartCard : StatelessWidget
{
    private readonly float _height;
    private readonly string? _title;
    private readonly float _window;
    private int _revision = int.MinValue;
    private float _lastNow = float.NegativeInfinity;

    public DevChartCard(Chart chart, float height, float windowSeconds = 0f, string? title = null)
    {
        Chart = chart;
        _height = height;
        _window = windowSeconds;
        _title = title;
    }

    public Chart Chart { get; }

    /// <summary>
    ///     Advance the chart each frame. A time-windowed card <b>glides</b>: it slides the rolling
    ///     x-window off the live clock (<paramref name="now" /> ticks ~60 Hz) every frame, not just when
    ///     a new sample lands — samples arrive in revision waves at only 1–4 Hz, so a revision-gated
    ///     shift would lurch the plot forward one push-interval at a time. A non-windowed card still
    ///     only re-resolves when <paramref name="revision" /> actually changed.
    /// </summary>
    public void Sync(int revision, float now, ThemeData theme)
    {
        Chart.Theme = theme;

        if (_window > 0f && Chart.XScale is LinearScale lin)
        {
            if (now == _lastNow && revision == _revision) return; // nothing moved this frame
            _lastNow = now;
            _revision = revision;
            lin.Min = now - _window;
            lin.Max = now;
            Chart.InvalidateData();
            return;
        }

        if (revision == _revision) return;
        _revision = revision;
        Chart.InvalidateData();
    }

    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        Chart.Theme = t;
        var col = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min);
        if (_title is not null)
            col.Children.Add(new Padding(EdgeInsets.Only(bottom: 2f),
                new Label(_title, DevKit.CaptionSize, t.Hint) { MaxLines = 1 }));
        col.Children.Add(new SizedBox(height: _height, child: Chart));
        return new Padding(EdgeInsets.Symmetric(0f, 3f), col);
    }
}
