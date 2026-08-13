using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     The reactive graph's churn (docs/architecture.md, "Diagnostics"): how much the app re-derives,
///     how much of that reaches the screen, and — with attribution on — which bodies are responsible.
///     <para>
///         The number to read is the <b>idle</b> one. A graph at rest should run nothing: a rate that
///         stays above zero while nothing on screen changes means a signal is being written with a
///         value
///         that only looks new (a value type without <see cref="IEquatable{T}" />, a computed
///         returning a
///         fresh collection every run), or an effect is writing a signal it reads.
///     </para>
/// </summary>
public sealed class ReactivePanel : IDevPanel
{
    private const int TopBodies = 8;
    private const float TableInterval = 0.5f;

    private static readonly Color Blue = Color.Rgb(r: 10, g: 132, b: 255);
    private static readonly Color Purple = Color.Rgb(r: 191, g: 90, b: 242);

    private readonly Label[] _bodies = new Label[TopBodies];
    private readonly CachedText[] _bodyText = new CachedText[TopBodies];
    private readonly DevChartCard _card;
    private readonly DevKeyValue _deferred = new("Deferred backlog");
    private readonly DevNote _hint = new("Turn attribution on, then reproduce the churn.");
    private readonly DevKeyValue _rate = new(key: "Runs / s", valueColor: Blue);
    private readonly DevKeyValue _rebuilds = new(key: "Watch rebuilds", valueColor: Purple);
    private readonly DevKeyValue _runs = new("Reaction runs");

    private readonly CachedText _tDeferred = new();
    private readonly CachedText _tRate = new();
    private readonly CachedText _tRebuilds = new();
    private readonly CachedText _tRuns = new();
    private readonly CachedText _tWrites = new();
    private readonly DevToggle _track;
    private readonly DevKeyValue _writes = new("Signal writes");
    private long _lastRuns;
    private float _rateTimer;

    private float _tableTimer;

    public ReactivePanel()
    {
        var chart = DevChart.Sparkline();
        AddLine(
            chart: chart,
            ring: DevChartData.ReactionRuns,
            name: "runs/s",
            color: Blue
        );
        AddLine(
            chart: chart,
            ring: DevChartData.WatchRebuilds,
            name: "rebuilds/s",
            color: Purple
        );
        _card = new DevChartCard(
            chart: chart,
            height: 84f,
            windowSeconds: 60f,
            title: "Graph churn — 60 s"
        );

        for (int i = 0; i < TopBodies; i++)
        {
            _bodies[i] = new Label(text: "", fontSize: DevKit.CaptionSize) {
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
                FontFamily = "code",
            };
            _bodyText[i] = new CachedText();
        }

        _track = new DevToggle(
            label: "Attribute runs to call sites",
            value: Reactive.TrackReactions,
            onChanged: SetTracking
        );
    }

    public string Title => "Reactive";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        var column = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new DevSectionHeader("Graph churn"),
                _card,
                _rate,
                _rebuilds,
                new DevSectionHeader("Totals"),
                _writes,
                _runs,
                _deferred,
                new DevSectionHeader("Hottest bodies"),
                _track,
                _hint,
            },
        };

        foreach (var row in _bodies) column.Children.Add(row);
        return column;
    }

    public void Refresh(float dt)
    {
        _card.Sync(
            revision: DevChartData.Revision,
            now: DevChartData.Time,
            theme: App.Active?.Theme ?? ThemeData.Dark
        );

        _writes.Value = _tWrites.Update($"{Reactive.Writes}");
        _runs.Value = _tRuns.Update($"{Reactive.Runs}");
        _rebuilds.Value = _tRebuilds.Update($"{Watch.Rebuilds}");
        _deferred.Value = _tDeferred.Update($"{Reactive.PendingDeferred}");

        // Own rate, on its own clock: the chart's ring only samples while the app renders, and this
        // readout should agree with what the panel's viewer sees right now.
        _rateTimer += dt;
        if (_rateTimer >= TableInterval)
        {
            long runs = Reactive.Runs;
            _rate.Value = _tRate.Update($"{(runs - _lastRuns) / _rateTimer:F0}");
            _lastRuns = runs;
            _rateTimer = 0f;
        }

        // HottestReactions sorts and allocates — twice a second, not per frame.
        _tableTimer += dt;
        if (_tableTimer < TableInterval) return;
        _tableTimer = 0f;
        RefreshTable();
    }

    private void RefreshTable()
    {
        if (!Reactive.TrackReactions)
        {
            _hint.Text = "Turn attribution on, then reproduce the churn.";
            foreach (var row in _bodies) row.Text = "";
            return;
        }

        var hottest = Reactive.HottestReactions(TopBodies);
        _hint.Text = hottest.Length == 0
            ? "Nothing has run since attribution was switched on."
            : "Runs since attribution was switched on, by declaring method.";

        for (int i = 0; i < _bodies.Length; i++)
        {
            _bodies[i].Text = i < hottest.Length
                ? _bodyText[i].Update($"{hottest[i].Runs}×  {hottest[i].Label}")
                : "";
        }
    }

    // Toggling on clears first, so the table answers "what churned while I did that" rather than
    // "what has run since boot" — the counts are only comparable within one reproduction.
    private void SetTracking(bool on)
    {
        Reactive.ResetReactionStats();
        Reactive.TrackReactions = on;
        RefreshTable();
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
