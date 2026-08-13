using Xunit;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Charts.Rendering;
using Zigote.UI.Charts.Scales;

namespace Zigote.Tests;

/// <summary>
///     Headless coverage of Zigote.UI.Charts: the scale/tick math, stacking, monotone interpolation
///     and arc geometry are pure logic; the Chart widget itself is exercised through Measure → Layout
///     → Paint into a real <see cref="PaintList" /> (whose NaN validation doubles as a canary) plus
///     synthetic pointer input for the hover pipeline.
/// </summary>
[Collection(
    "Ticker"
)] // static Ticker.Active is shared; AdvanceAll in one class ticks another class's widgets
public class ChartsTests
{
    private static readonly List<Sale> Sales = [
        new(Month: "Jan", Revenue: 120, Region: "West"),
        new(Month: "Feb", Revenue: 180, Region: "West"),
        new(Month: "Mar", Revenue: 90, Region: "West"),
        new(Month: "Jan", Revenue: 60, Region: "East"),
        new(Month: "Feb", Revenue: 40, Region: "East"),
        new(Month: "Mar", Revenue: 150, Region: "East"),
    ];
    // ── NiceScale ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 5, 20)]
    [InlineData(1, 5, 0.2)]
    [InlineData(7, 5, 1)]
    [InlineData(0.55, 5, 0.1)]
    public void NiceScale_TickStep_Uses125Ladder(double range, int target, double expected) =>
        Assert.Equal(
            expected: expected,
            actual: NiceScale.TickStep(range: range, targetTicks: target),
            precision: 9
        );

    [Fact]
    public void NiceScale_NiceDomain_RoundsOutward()
    {
        (double min, double max, double step) = NiceScale.NiceDomain(
            min: 0.13,
            max: 9.8,
            targetTicks: 5
        );
        Assert.Equal(expected: 0, actual: min, precision: 9);
        Assert.Equal(expected: 10, actual: max, precision: 9);
        Assert.Equal(expected: 2, actual: step, precision: 9);
    }

    [Theory]
    [InlineData(1500, "1.5K")]
    [InlineData(2_300_000, "2.3M")]
    [InlineData(950, "950")]
    [InlineData(0.5, "0.5")]
    public void NiceScale_FormatNumber_Compacts(double v, string expected) => Assert.Equal(
        expected: expected,
        actual: NiceScale.FormatNumber(v)
    );

    // ── Scales ────────────────────────────────────────────────────────────────

    [Fact]
    public void LinearScale_NiceDomain_AndNormalize()
    {
        var s = new LinearScale { IncludeZero = true };
        s.Include(3);
        s.Include(97);
        s.FinalizeDomain();

        Assert.Equal(expected: 0, actual: s.DomainMin, precision: 6);
        Assert.Equal(expected: 100, actual: s.DomainMax, precision: 6);
        Assert.Equal(expected: 0.5f, actual: s.NormalizeNumeric(50), precision: 3);

        var ticks = s.BuildTicks(targetCount: 5, formatter: null);
        Assert.True(ticks.Count >= 4);
        Assert.All(
            collection: ticks,
            action: t => Assert.InRange(actual: t.Position, low: -0.001f, high: 1.001f)
        );
    }

    [Fact]
    public void LinearScale_ExplicitBounds_Win()
    {
        var s = new LinearScale {
            Min = -10,
            Max = 10,
        };
        s.Include(500);
        s.FinalizeDomain();
        Assert.Equal(expected: -10, actual: s.DomainMin, precision: 6);
        Assert.Equal(expected: 10, actual: s.DomainMax, precision: 6);
    }

    [Fact]
    public void LinearScale_Reset_AllowsRebuild()
    {
        var s = new LinearScale();
        s.Include(0);
        s.Include(10);
        s.FinalizeDomain();
        s.Reset();
        s.Include(100);
        s.Include(200);
        s.FinalizeDomain();
        Assert.True(s.DomainMax >= 200);
        Assert.True(s.DomainMin >= 50); // old 0..10 domain must not leak in
    }

    [Fact]
    public void BandScale_CentersAndThinning()
    {
        var s = new BandScale();
        s.Include("A");
        s.Include("B");
        s.Include("A"); // duplicate keeps first slot
        s.FinalizeDomain();

        Assert.Equal(expected: 2, actual: s.Categories.Count);
        Assert.Equal(expected: 0.25f, actual: s.Normalize("A"), precision: 3);
        Assert.Equal(expected: 0.75f, actual: s.Normalize("B"), precision: 3);
        Assert.Equal(expected: 0.5f, actual: s.NormalizedBandWidth, precision: 3);

        var wide = new BandScale();
        for (int i = 0; i < 30; i++) wide.Include($"c{i}");
        wide.FinalizeDomain();
        Assert.True(
            wide.BuildTicks(targetCount: 6, formatter: null).Count <= 8
        ); // thinned, not 30 labels
    }

    [Fact]
    public void LogScale_NormalizesDecades()
    {
        var s = new LogScale();
        s.Include(1);
        s.Include(1000);
        s.FinalizeDomain();
        Assert.Equal(expected: 0f, actual: s.NormalizeNumeric(1), precision: 3);
        Assert.Equal(expected: 1f, actual: s.NormalizeNumeric(1000), precision: 3);
        Assert.Equal(expected: 1f / 3f, actual: s.NormalizeNumeric(10), precision: 3);
    }

    [Fact]
    public void TimeScale_PicksCalendarUnits()
    {
        var s = new TimeScale();
        s.Include(new DateTime(year: 2026, month: 1, day: 15));
        s.Include(new DateTime(year: 2026, month: 7, day: 15));
        s.FinalizeDomain();

        var ticks = s.BuildTicks(targetCount: 6, formatter: null);
        Assert.True(ticks.Count is >= 4 and <= 9);
        Assert.Contains(
            collection: ticks,
            filter: t => t.Label == "Feb"
        ); // month-aligned labels

        var day = new TimeScale();
        day.Include(
            new DateTime(
                year: 2026,
                month: 3,
                day: 1,
                hour: 0,
                minute: 0,
                second: 0
            )
        );
        day.Include(
            new DateTime(
                year: 2026,
                month: 3,
                day: 1,
                hour: 23,
                minute: 59,
                second: 0
            )
        );
        day.FinalizeDomain();
        Assert.Contains(
            collection: day.BuildTicks(targetCount: 6, formatter: null),
            filter: t => t.Label.Contains(':')
        ); // HH:mm labels
    }

    // ── Stacking ──────────────────────────────────────────────────────────────

    [Fact]
    public void StackCompute_Standard_DivergesAtZero()
    {
        var spans = new Dictionary<(string, ChartValue), StackedSpan>();
        StackCompute.Compute(
            points: [("a", "x", 3.0), ("b", "x", 2.0), ("c", "x", -4.0)],
            seriesOrder: ["a", "b", "c"],
            mode: ChartStacking.Standard,
            result: spans
        );

        Assert.Equal(expected: new StackedSpan(Bottom: 0, Top: 3), actual: spans[("a", "x")]);
        Assert.Equal(expected: new StackedSpan(Bottom: 3, Top: 5), actual: spans[("b", "x")]);
        Assert.Equal(expected: new StackedSpan(Bottom: -4, Top: 0), actual: spans[("c", "x")]);
    }

    [Fact]
    public void StackCompute_Normalized_SumsToOne()
    {
        var spans = new Dictionary<(string, ChartValue), StackedSpan>();
        StackCompute.Compute(
            points: [("a", "x", 1.0), ("b", "x", 3.0)],
            seriesOrder: ["a", "b"],
            mode: ChartStacking.Normalized,
            result: spans
        );

        Assert.Equal(expected: 0.25, actual: spans[("a", "x")].Value, precision: 9);
        Assert.Equal(expected: 0.75, actual: spans[("b", "x")].Value, precision: 9);
        Assert.Equal(expected: 1.0, actual: spans[("b", "x")].Top, precision: 9);
    }

    [Fact]
    public void StackCompute_Center_Silhouette()
    {
        var spans = new Dictionary<(string, ChartValue), StackedSpan>();
        StackCompute.Compute(
            points: [("a", "x", 2.0), ("b", "x", 2.0)],
            seriesOrder: ["a", "b"],
            mode: ChartStacking.Center,
            result: spans
        );

        Assert.Equal(expected: -2.0, actual: spans[("a", "x")].Bottom, precision: 9);
        Assert.Equal(expected: 2.0, actual: spans[("b", "x")].Top, precision: 9);
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    [Fact]
    public void Monotone_NeverOvershoots()
    {
        float[] xs = [0, 1, 2, 3, 4];
        float[] ys = [0, 10, 10, 0, 0];
        float[] slopes = ChartGeometry.MonotoneSlopes(xs: xs, ys: ys);

        // Flat segments must stay flat (no wiggle past the data range).
        for (float x = 1.0f; x <= 2.0f; x += 0.1f)
        {
            Assert.InRange(
                actual: ChartGeometry.EvaluateMonotone(
                    xs: xs,
                    ys: ys,
                    slopes: slopes,
                    x: x
                ),
                low: 9.999f,
                high: 10.001f
            );
        }

        for (float x = 0.0f; x <= 4.0f; x += 0.05f)
        {
            Assert.InRange(
                actual: ChartGeometry.EvaluateMonotone(
                    xs: xs,
                    ys: ys,
                    slopes: slopes,
                    x: x
                ),
                low: -0.001f,
                high: 10.001f
            );
        }
    }

    [Fact]
    public void ArcToCubics_EndpointsOnCircle()
    {
        // Quarter arc from 12 o'clock to 3 o'clock around (0,0) r=10.
        var cubics = ChartGeometry.ArcToCubics(
            cx: 0,
            cy: 0,
            radius: 10,
            startAngle: 0,
            endAngle: MathF.PI / 2f
        );
        Assert.Single(cubics);
        var c = cubics[0];
        Assert.Equal(expected: 0f, actual: c.X0, precision: 3);
        Assert.Equal(expected: -10f, actual: c.Y0, precision: 3);
        Assert.Equal(expected: 10f, actual: c.X3, precision: 3);
        Assert.Equal(expected: 0f, actual: c.Y3, precision: 3);

        // A full half circle splits into two segments.
        Assert.Equal(
            expected: 2,
            actual: ChartGeometry.ArcToCubics(
                cx: 0,
                cy: 0,
                radius: 10,
                startAngle: 0,
                endAngle: MathF.PI
            ).Count
        );
    }

    private static Chart LaidOut(Chart chart, float w = 600, float h = 300)
    {
        chart.Measure(Constraints.Tight(width: w, height: h));
        chart.Layout(new Offset(x: 0, y: 0));
        return chart;
    }

    [Fact]
    public void Chart_ComposesMarks_SharedScales_AndPaints()
    {
        var chart = new Chart {
            Marks = {
                BarMark.Of(data: Sales, x: d => d.Month, y: d => d.Revenue),
                new RuleMark {
                    Y = 200,
                    Label = "Target",
                },
            },
        };
        chart.Marks.OfType<BarMark<Sale>>().First().SeriesBy = d => d.Region;
        LaidOut(chart);

        // Plot carved inside the widget, axes resolved: x = 3 month bands, y = linear from 0.
        Assert.True(chart.PlotRect.Width > 400 && chart.PlotRect.Height > 180);
        Assert.IsType<BandScale>(chart.ResolvedXScale);
        var y = Assert.IsType<LinearScale>(chart.ResolvedYScale);
        Assert.Equal(expected: 0, actual: y.DomainMin, precision: 6);
        Assert.True(y.DomainMax >= 200); // stacked column (180+40=220) and the rule fit

        Assert.Equal(expected: 3, actual: chart.XTicks.Count);
        Assert.Equal(expected: 2, actual: chart.LegendEntries.Count); // West + East

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate(); // balanced clips, no NaN
        Assert.True(paint.Count > 20);
    }

    [Fact]
    public void Chart_Hover_ResolvesColumnCluster_AndTapFires()
    {
        var chart = new Chart {
            Marks = { LineMark.Of(data: Sales, x: d => d.Month, y: d => d.Revenue) },
        };
        ((LineMark<Sale>)chart.Marks[0]).SeriesBy = d => d.Region;
        LaidOut(chart);

        // Hover the middle of the plot: both series report a point at the nearest month.
        var mid = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + (chart.PlotRect.Height / 2f)
        );
        chart.OnPointerMove(mid);
        var hover = chart.CurrentHover;
        Assert.NotNull(hover);
        Assert.Equal(expected: 2, actual: hover.Points.Count);
        Assert.Equal(expected: "Feb", actual: hover.XLabel);

        ChartHoverInfo? tapped = null;
        chart.OnPointTap = info => tapped = info;
        chart.OnPointerDown(mid);
        chart.OnPointerUp(mid); // taps resolve on release so drag-pans don't fire them
        Assert.NotNull(tapped);

        chart.OnPointerExit();
        Assert.Null(chart.CurrentHover);

        // Tooltip paints on the hover path too.
        chart.OnPointerMove(mid);
        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
    }

    [Fact]
    public void Chart_Hover_WorksAfterInvalidate_LazyRegistry()
    {
        // The hover registry is collected lazily on the first query after a relayout — hovering
        // right after an InvalidateData + layout must still resolve points.
        var data = Enumerable.Range(start: 0, count: 50)
            .Select(i => ((double)i, Math.Sin(i / 5.0))).ToList();
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
        };
        LaidOut(chart);
        chart.InvalidateData();
        LaidOut(chart);

        var mid = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + (chart.PlotRect.Height / 2f)
        );
        chart.OnPointerMove(mid);
        Assert.NotNull(chart.CurrentHover);
        Assert.Single(chart.CurrentHover.Points);
    }

    [Fact]
    public void Chart_Hover_PersistsAcrossLiveInvalidate()
    {
        // An actively-updating chart re-resolves the hover under the last pointer position on
        // relayout instead of dropping the overlay.
        var data = Enumerable.Range(start: 0, count: 50)
            .Select(i => ((double)i, Math.Sin(i / 5.0))).ToList();
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
        };
        LaidOut(chart);

        var mid = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + (chart.PlotRect.Height / 2f)
        );
        chart.OnPointerMove(mid);
        Assert.NotNull(chart.CurrentHover);

        chart.InvalidateData();
        LaidOut(chart);
        Assert.NotNull(chart.CurrentHover);

        // Once the pointer leaves, a later data update must not resurrect the hover.
        chart.OnPointerExit();
        chart.InvalidateData();
        LaidOut(chart);
        Assert.Null(chart.CurrentHover);
    }

    [Fact]
    public void Chart_PinOnTap_PersistsAcrossUpdates_TogglesAndDrops()
    {
        var data = Enumerable.Range(start: 0, count: 50)
            .Select(i => ((double)i, Math.Sin(i / 5.0))).ToList();
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
        };
        LaidOut(chart);
        var pins = new List<ChartHoverInfo?>();
        chart.OnPinChanged = p => pins.Add(p);

        var mid = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + (chart.PlotRect.Height / 2f)
        );
        chart.OnPointerMove(mid);
        chart.OnPointerDown(mid);
        chart.OnPointerUp(mid); // tap pins the hovered column
        Assert.NotNull(chart.PinnedHover);
        Assert.Single(pins);
        var pinnedX = chart.PinnedHover.X;

        // Survives pointer exit and live data updates, still anchored to the same data x.
        chart.OnPointerExit();
        Assert.Null(chart.CurrentHover);
        Assert.NotNull(chart.PinnedHover);
        chart.InvalidateData();
        LaidOut(chart);
        Assert.NotNull(chart.PinnedHover);
        Assert.Equal(expected: pinnedX, actual: chart.PinnedHover.X);

        // The pinned overlay paints (accent crosshair + pin dot + column-anchored tooltip).
        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 20);

        // Tapping the same column unpins.
        chart.OnPointerMove(mid);
        chart.OnPointerDown(mid);
        chart.OnPointerUp(mid);
        Assert.Null(chart.PinnedHover);
        Assert.Equal(expected: 2, actual: pins.Count);
        Assert.Null(pins[1]);

        // Re-pin, then shift the data so the pinned x no longer exists → the pin drops.
        chart.OnPointerMove(mid);
        chart.OnPointerDown(mid);
        chart.OnPointerUp(mid);
        Assert.NotNull(chart.PinnedHover);
        data.Clear();
        data.AddRange(
            Enumerable.Range(start: 1000, count: 50).Select(i => ((double)i, Math.Sin(i / 5.0)))
        );
        chart.InvalidateData();
        LaidOut(chart);
        Assert.Null(chart.PinnedHover);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("area")]
    public void Chart_LiveInvalidate_ResolvePath_AllocatesLittle(string kind)
    {
        // A live chart (LiveLineChart / DevTools sparklines) re-resolves many times a second. The
        // hover registry must NOT be rebuilt per invalidate (lazy), the resolve scratch
        // (_resolved/_seriesOrder/groups/triples/spans) must be reused, and stacking keys are the
        // raw ChartValues (no per-point key strings) — this was ~150+ KB per invalidate.
        var data = Enumerable.Range(start: 0, count: 500)
            .Select(i => ((double)i, Math.Sin(i / 15.0)))
            .ToList();
        ChartMark mark = kind == "area"
            ? AreaMark.Of(data: data, x: d => d.Item1, y: d => d.Item2)
            : LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2);
        var chart = new Chart {
            Animated = false,
            Marks = { mark },
        };
        LaidOut(chart);
        for (int i = 0; i < 50; i++)
        {
            chart.InvalidateData();
            LaidOut(chart);
        }

        const int rounds = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < rounds; i++)
        {
            chart.InvalidateData();
            LaidOut(chart);
        }

        long perRound = (GC.GetAllocatedBytesForCurrentThread() - before) / rounds;
        Assert.True(
            condition: perRound < 8_000,
            userMessage:
            $"Resolve path allocated {perRound} B/invalidate; expected well under 8 KB " +
            "(ticks/labels only — no hover-registry rebuild, no fresh resolve collections)."
        );
    }

    [Fact]
    public void Chart_HorizontalBars_WhenYIsCategory()
    {
        var chart = new Chart {
            Marks = { BarMark.Of(data: Sales, x: d => d.Revenue, y: d => d.Month) },
        };
        LaidOut(chart);

        Assert.IsType<LinearScale>(chart.ResolvedXScale);
        Assert.IsType<BandScale>(chart.ResolvedYScale);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 5);
    }

    [Fact]
    public void Chart_StackedArea_ExtendsDomainToStackTop()
    {
        var area = AreaMark.Of(data: Sales, x: d => d.Month, y: d => d.Revenue);
        area.SeriesBy = d => d.Region;
        var chart = new Chart { Marks = { area } };
        LaidOut(chart);

        var y = Assert.IsType<LinearScale>(chart.ResolvedYScale);
        Assert.True(y.DomainMax >= 220); // Feb stack: 180 + 40

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 50); // strip fill emits many rects
    }

    [Fact]
    public void Chart_Sectors_PolarHitTest_AndNoAxes()
    {
        var data = new List<(string Name, double Value)> {
            ("A", 50),
            ("B", 30),
            ("C", 20),
        };
        var chart = new Chart {
            Marks = { SectorMark.Of(data: data, value: d => d.Value, category: d => d.Name) },
        };
        LaidOut(chart: chart, w: 400);

        Assert.Empty(chart.XTicks); // polar-only chart hides the cartesian axes
        Assert.Equal(expected: 3, actual: chart.LegendEntries.Count);

        // 'A' spans the first half of the sweep; just right of 12 o'clock lands inside it.
        float cx = chart.PlotRect.X + (chart.PlotRect.Width / 2f);
        float cy = chart.PlotRect.Y + (chart.PlotRect.Height / 2f);
        float r = MathF.Min(x: chart.PlotRect.Width, y: chart.PlotRect.Height) / 2f * 0.7f;
        chart.OnPointerMove(new Offset(x: cx + (r * 0.5f), y: cy - (r * 0.5f)));
        var hover = chart.CurrentHover;
        Assert.NotNull(hover);
        Assert.Equal(expected: "A", actual: hover.XLabel);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 10);
    }

    [Fact]
    public void Chart_TimeSeries_PointsAndLog()
    {
        var start = new DateTime(year: 2026, month: 1, day: 1);
        var series = Enumerable.Range(start: 0, count: 90)
            .Select(i => (Day: start.AddDays(i), Value: Math.Pow(x: 10, y: 1 + (i / 45.0))))
            .ToList();

        var chart = new Chart {
            YScale = new LogScale(),
            Marks = {
                LineMark.Of(data: series, x: d => d.Day, y: d => d.Value),
                PointMark.Of(data: series, x: d => d.Day, y: d => d.Value),
            },
        };
        LaidOut(chart);

        Assert.IsType<TimeScale>(chart.ResolvedXScale);
        Assert.IsType<LogScale>(chart.ResolvedYScale);
        Assert.NotEmpty(chart.XTicks);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 90); // 90 symbols + line segments + axes
    }

    // ── Scrolling ─────────────────────────────────────────────────────────────

    [Fact]
    public void LinearScale_VisibleWindow_RemapsAndTicks()
    {
        var s = new LinearScale { IncludeZero = true };
        s.Include(0);
        s.Include(100);
        s.FinalizeDomain();
        Assert.Equal(expected: (0.0, 100.0), actual: s.FullExtent);

        s.SetVisibleWindow(min: 20, max: 40);
        Assert.Equal(expected: 0.5f, actual: s.NormalizeNumeric(30), precision: 3);
        Assert.All(
            collection: s.BuildTicks(targetCount: 5, formatter: null),
            action: t => Assert.InRange(actual: t.Position, low: -0.001f, high: 1.001f)
        );
    }

    [Fact]
    public void BandScale_VisibleWindow_ShowsIndexRange()
    {
        var s = new BandScale();
        for (int i = 0; i < 10; i++) s.Include($"c{i}");
        s.FinalizeDomain();

        s.SetVisibleWindow(min: 2, max: 6); // categories 2..5 visible
        Assert.Equal(expected: 0.25f, actual: s.NormalizedBandWidth, precision: 3);
        Assert.Equal(
            expected: 0.375f,
            actual: s.Normalize("c3"),
            precision: 3
        ); // (3.5 - 2) / 4
        Assert.All(
            collection: s.BuildTicks(targetCount: 10, formatter: null),
            action: t => Assert.InRange(actual: t.Position, low: -0.03f, high: 1.03f)
        );
    }

    [Fact]
    public void Chart_ScrollableX_StartsAtEnd_PansAndClamps()
    {
        var data = Enumerable.Range(start: 0, count: 100)
            .Select(i => ((double)i, Math.Sin(i / 5.0)))
            .ToList();
        var chart = new Chart {
            Animated = false,
            ScrollableX = true,
            VisibleXDomainLength = 20.0,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
        };
        LaidOut(chart);

        // Nice domain of 0..99 is 0..100 → the initial window sticks to the newest data.
        Assert.Equal(expected: 80.0, actual: chart.ScrollOffsetX, precision: 2);

        chart.ScrollOffsetX = 30;
        LaidOut(chart);
        var x = Assert.IsType<LinearScale>(chart.ResolvedXScale);
        Assert.Equal(expected: 0.5f, actual: x.NormalizeNumeric(40), precision: 3);

        // Drag right by 100px → window pans toward earlier data, tap suppressed.
        ChartHoverInfo? tapped = null;
        chart.OnPointTap = i => tapped = i;
        var mid = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + (chart.PlotRect.Height / 2f)
        );
        chart.OnPointerDown(mid);
        chart.OnPointerMove(new Offset(x: mid.X + 100f, y: mid.Y));
        chart.OnPointerUp(new Offset(x: mid.X + 100f, y: mid.Y));

        double expected = 30.0 - (100.0 * 20.0 / chart.PlotRect.Width);
        Assert.Equal(expected: expected, actual: chart.ScrollOffsetX, precision: 2);
        Assert.Null(tapped);
        Assert.Null(chart.CurrentHover);

        // Clamp at the front edge.
        chart.ScrollOffsetX = -500;
        LaidOut(chart);
        Assert.Equal(expected: 0.0, actual: chart.ScrollOffsetX, precision: 2);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate(); // includes the scroll indicator
    }

    [Fact]
    public void Chart_ScrollableY_WindowsValueAxis_AndPansWithVerticalDrag()
    {
        var data = Enumerable.Range(start: 0, count: 100).Select(i => ((double)i, (double)i))
            .ToList();
        var chart = new Chart {
            Animated = false,
            ScrollableY = true,
            VisibleYDomainLength = 20.0,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
        };
        LaidOut(chart);

        // Y is not stick-to-end → starts at the bottom of the (nice) domain.
        Assert.Equal(expected: 0.0, actual: chart.ScrollOffsetY, precision: 2);
        var y = Assert.IsType<LinearScale>(chart.ResolvedYScale);
        Assert.Equal(expected: 0f, actual: y.NormalizeNumeric(0), precision: 3);
        Assert.Equal(expected: 1f, actual: y.NormalizeNumeric(20), precision: 3);

        // Dragging down reveals higher values (offset increases).
        var mid = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + (chart.PlotRect.Height / 2f)
        );
        chart.OnPointerDown(mid);
        chart.OnPointerMove(new Offset(x: mid.X, y: mid.Y + (chart.PlotRect.Height / 2f)));
        chart.OnPointerUp(new Offset(x: mid.X, y: mid.Y + (chart.PlotRect.Height / 2f)));
        Assert.True(chart.ScrollOffsetY > 0.0);
    }

    [Fact]
    public void Chart_ZoomBy_ShrinksWindow_KeepingFocusPoint()
    {
        var data = Enumerable.Range(start: 0, count: 100)
            .Select(i => ((double)i, Math.Sin(i / 5.0)))
            .ToList();
        var chart = new Chart {
            Animated = false,
            ZoomableX = true,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
        };
        LaidOut(chart);
        Assert.IsType<LinearScale>(chart.ResolvedXScale);

        // Zoom 2× around the plot centre (domain ~50 for a 0..100 nice domain).
        var centre = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + 10f
        );
        chart.ZoomBy(factor: 2.0, focus: centre);
        LaidOut(chart);
        var x = Assert.IsType<LinearScale>(chart.ResolvedXScale);
        // The visible window halved (0..100 → ~25..75), so 50 stays centred.
        Assert.Equal(expected: 0.5f, actual: x.NormalizeNumeric(50), precision: 2);
        Assert.True(x.NormalizeNumeric(24) < 0f); // 24 now off the left edge

        chart.ResetZoom();
        LaidOut(chart);
        var x2 = Assert.IsType<LinearScale>(chart.ResolvedXScale);
        Assert.True(x2.NormalizeNumeric(10) is > 0f and < 0.2f); // full extent visible again
    }

    // ── X range selection ─────────────────────────────────────────────────────

    [Fact]
    public void Chart_XSelection_ReportsDomainRange_AndClearsOnClick()
    {
        var data = Enumerable.Range(start: 0, count: 50).Select(i => ((double)i, (double)i))
            .ToList();
        var chart = new Chart {
            Animated = false,
            EnableXSelection = true,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
        };
        LaidOut(chart);

        (double Min, double Max)? reported = null;
        chart.OnXRangeSelected = r => reported = r;

        var plot = chart.PlotRect;
        float x0 = plot.X + (plot.Width * 0.25f);
        float x1 = plot.X + (plot.Width * 0.75f);
        float y = plot.Y + (plot.Height / 2f);
        chart.OnPointerDown(new Offset(x: x0, y: y));
        chart.OnPointerMove(new Offset(x: x1, y: y));
        chart.OnPointerUp(new Offset(x: x1, y: y));

        Assert.NotNull(reported);
        Assert.NotNull(chart.SelectedXRange);
        // Nice domain 0..49 → 0..50; the quarter/three-quarter marks map to ~12.5 and ~37.5.
        Assert.InRange(actual: chart.SelectedXRange!.Value.Min, low: 10.0, high: 15.0);
        Assert.InRange(actual: chart.SelectedXRange!.Value.Max, low: 35.0, high: 40.0);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate(); // selection band drawn

        // A click (no drag) clears it.
        chart.OnPointerDown(new Offset(x: x0, y: y));
        chart.OnPointerUp(new Offset(x: x0 + 1f, y: y));
        Assert.Null(chart.SelectedXRange);
        Assert.Null(reported);
    }

    // ── Dual y-axes ───────────────────────────────────────────────────────────

    [Fact]
    public void Chart_DualYAxes_IndependentScales_SharedSeriesColors()
    {
        var priceData = Enumerable.Range(start: 0, count: 12)
            .Select(i => ((double)i, 100.0 + i)).ToList();
        var volData = Enumerable.Range(start: 0, count: 12)
            .Select(i => ((double)i, (double)(i * 1000)))
            .ToList();

        var price = LineMark.Of(data: priceData, x: d => d.Item1, y: d => d.Item2);
        price.Name = "price";
        var vol = BarMark.Of(data: volData, x: d => d.Item1, y: d => d.Item2);
        vol.Name = "volume";
        vol.UseSecondaryYAxis = true;

        var chart = new Chart {
            Animated = false,
            Marks = {
                vol,
                price,
            },
        };
        LaidOut(chart);

        var primary = Assert.IsType<LinearScale>(chart.ResolvedYScale);
        var secondary = Assert.IsType<LinearScale>(chart.ResolvedSecondaryYScale);
        // Primary spans ~100..111, secondary ~0..11000 — clearly different domains.
        Assert.True(primary.DomainMax <= 130);
        Assert.True(secondary.DomainMax >= 11000);
        Assert.NotEmpty(chart.SecondaryYTicks);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 20);
    }

    // ── Annotations ───────────────────────────────────────────────────────────

    [Fact]
    public void Chart_Annotation_ProjectsToDataPosition_AndPaints()
    {
        var data = Enumerable.Range(start: 0, count: 10).Select(i => ((double)i, (double)i))
            .ToList();
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
            Annotations = {
                new ChartAnnotation {
                    X = 5.0,
                    Y = 5.0,
                    Text = "peak",
                    Placement = ChartAnnotationPlacement.Above,
                },
            },
        };
        LaidOut(chart);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        // A text command for the annotation label is present.
        Assert.Contains(
            collection: paint.DebugCommands,
            filter: c => c.Kind == (byte)PaintCommandKind.Text
        );
    }

    // ── Decimation / LOD ──────────────────────────────────────────────────────

    [Fact]
    public void Lttb_PreservesEndpointsAndExtremes()
    {
        int n = 1000;
        float[] xs = new float[n];
        float[] ys = new float[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = i;
            ys[i] = MathF.Sin(i / 20f);
        }

        ys[500] = 99f; // a spike the decimation must keep

        int[]? idx = ChartGeometry.LttbIndices(xs: xs, ys: ys, threshold: 50);
        Assert.NotNull(idx);
        Assert.Equal(expected: 50, actual: idx!.Length);
        Assert.Equal(expected: 0, actual: idx[0]);
        Assert.Equal(expected: n - 1, actual: idx[^1]);
        Assert.Contains(expected: 500, collection: idx); // the spike survived
        // Indices strictly ascending.
        for (int i = 1; i < idx.Length; i++) Assert.True(idx[i] > idx[i - 1]);

        // Already-small series pass through untouched.
        Assert.Null(
            ChartGeometry.LttbIndices(
                xs: xs.AsSpan(start: 0, length: 10),
                ys: ys.AsSpan(start: 0, length: 10),
                threshold: 50
            )
        );
    }

    [Fact]
    public void Chart_LineMark_MaxRenderPoints_CutsStrokeCommands()
    {
        var data = Enumerable.Range(start: 0, count: 2000)
            .Select(i => ((double)i, Math.Sin(i / 30.0)))
            .ToList();

        int PaintCommandCount(int cap)
        {
            var line = LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2);
            line.Interpolation = ChartInterpolation.Linear;
            line.MaxRenderPoints = cap;
            var chart = new Chart {
                Animated = false,
                Marks = { line },
            };
            LaidOut(chart);
            var paint = new PaintList();
            chart.Paint(paint);
            paint.Validate();
            return paint.Count;
        }

        int full = PaintCommandCount(0);
        int capped = PaintCommandCount(100);
        Assert.True(
            condition: capped < full / 2,
            userMessage: $"decimated ({capped}) should be far below full ({full})"
        );
    }

    [Theory]
    [InlineData("monotone-line")]
    [InlineData("linear-line")]
    [InlineData("area")]
    [InlineData("bars")]
    [InlineData("points")]
    [InlineData("function")]
    [InlineData("styled-axis")]
    [InlineData("overlay")]
    public void Chart_SteadyStatePaint_AllocatesZero(string kind)
    {
        // A live chart (LiveLineChart-style) repaints every frame, so the whole mark paint path must
        // be zero-alloc in steady state: slopes in a reused scratch, render-order caches without
        // hoisted sort closures, bar keys resolved once per data resolve, no boxed enumerators.
        var data = Enumerable.Range(start: 0, count: 200)
            .Select(i => ((double)i, Math.Sin(i / 15.0)))
            .ToList();
        var linear = LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2);
        linear.Interpolation = ChartInterpolation.Linear;
        ChartMark mark = kind switch {
            "monotone-line" => LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2),
            "linear-line" => linear,
            "area" => AreaMark.Of(data: data, x: d => d.Item1, y: d => d.Item2),
            "bars" => BarMark.Of(
                data: data.Take(12).ToList(),
                x: d => d.Item1,
                y: d => d.Item2
            ),
            "function" => new FunctionLineMark(
                function: static x => Math.Sin(x / 15.0),
                xMin: 0,
                xMax: 200
            ),
            _ => PointMark.Of(data: data, x: d => d.Item1, y: d => d.Item2),
        };
        var chart = new Chart {
            Animated = false,
            Marks = { mark },
        };
        if (kind == "styled-axis")
        {
            chart.YAxis.TickValues = [-1.0, -0.5, 0.0, 0.5, 1.0];
            chart.YAxis.TickStyle = static v => v.Numeric == 0
                ? new AxisTickStyle { GridWidth = 2f }
                : default;
            chart.XAxis.TickStyle = static v => v.Numeric > 150
                ? new AxisTickStyle { HideLabel = true }
                : default;
        }

        if (kind == "overlay")
        {
            chart.OverlayPainter = static (p, proxy) => p.AddRect(
                bounds: new Rect(
                    x: proxy.PlotRect.X,
                    y: proxy.PositionY(0.5),
                    width: proxy.PlotRect.Width,
                    height: 2f
                ),
                color: Color.Rgb(r: 52, g: 199, b: 89)
            );
        }

        LaidOut(chart);
        var paint = new PaintList();

        // Warm up past tiered JIT and populate the Utf8 / TextMeasure / PaintList-capacity caches.
        for (int i = 0; i < 200; i++)
        {
            paint.Clear();
            chart.Paint(paint);
        }

        Assert.True(paint.Count > 10);

        const int frames = 300;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            paint.Clear();
            chart.Paint(paint);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(
            condition: allocated == 0,
            userMessage: $"{kind} chart paint allocated {allocated} B over {frames} frames " +
                         $"({allocated / (double)frames:F2} B/frame); expected 0."
        );
    }

    // ── Inverse mapping (NumericAt) + ChartProxy ──────────────────────────────

    [Fact]
    public void Scales_NumericAt_InvertsNormalize()
    {
        var linear = new LinearScale {
            Min = 10,
            Max = 30,
            Nice = false,
        };
        linear.FinalizeDomain();
        Assert.Equal(
            expected: 20,
            actual: linear.NumericAt(linear.NormalizeNumeric(20)),
            precision: 9
        );
        Assert.Equal(expected: 10, actual: linear.NumericAt(0f), precision: 9);
        Assert.Equal(expected: 30, actual: linear.NumericAt(1f), precision: 9);

        var log = new LogScale {
            Min = 1,
            Max = 1000,
        };
        log.FinalizeDomain();
        // Round trip through a float normalize + pow10 — compare at float precision.
        Assert.Equal(
            expected: 100,
            actual: log.NumericAt(log.NormalizeNumeric(100)),
            precision: 2
        );

        var band = new BandScale();
        band.Include("A");
        band.Include("B");
        band.Include("C");
        band.FinalizeDomain();
        // Band 1's centre normalizes to 0.5 → NumericAt gives back the band-index magnitude 1.5.
        Assert.Equal(
            expected: 1.5,
            actual: band.NumericAt(band.Normalize(ChartValue.Category("B"))),
            precision: 6
        );
    }

    [Fact]
    public void ChartProxy_RoundTrips_DataAndScreen()
    {
        Assert.False(new Chart().Proxy.IsValid); // no layout yet — nothing to project through

        var data = new List<(double X, double Y)> {
            (0, 0),
            (10, 100),
        };
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data: data, x: d => d.X, y: d => d.Y) },
            XScale = new LinearScale {
                Min = 0,
                Max = 10,
                Nice = false,
            },
            YScale = new LinearScale {
                Min = 0,
                Max = 100,
                Nice = false,
            },
        };
        LaidOut(chart);

        var proxy = chart.Proxy;
        Assert.True(proxy.IsValid);
        Assert.Equal(
            expected: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            actual: proxy.PositionX(5),
            precision: 2
        );
        Assert.Equal(expected: chart.PlotRect.Bottom, actual: proxy.PositionY(0), precision: 2);
        Assert.Equal(expected: chart.PlotRect.Y, actual: proxy.PositionY(100), precision: 2);

        // Screen → domain inverts the projection.
        Assert.Equal(expected: 5, actual: proxy.XValueAt(proxy.PositionX(5)), precision: 4);
        Assert.Equal(expected: 42, actual: proxy.YValueAt(proxy.PositionY(42)), precision: 3);

        var p = proxy.Position(x: 5, y: 50);
        Assert.Equal(expected: proxy.PositionX(5), actual: p.X, precision: 3);
        Assert.Equal(expected: proxy.PositionY(50), actual: p.Y, precision: 3);
    }

    // ── Axis customization (TickValues + TickStyle) ───────────────────────────

    [Fact]
    public void Chart_CustomTickValues_PinTheAxisTicks()
    {
        var chart = new Chart {
            Animated = false,
            Marks = { BarMark.Of(data: Sales, x: d => d.Month, y: d => d.Revenue) },
            YScale = new LinearScale {
                Min = 0,
                Max = 300,
                Nice = false,
            },
            YAxis = {
                TickValues = [0.0, 150.0, 300.0],
                Formatter = v => $"{v.Numeric:F0}u",
            },
        };
        LaidOut(chart);

        Assert.Equal(expected: 3, actual: chart.YTicks.Count);
        Assert.Equal(expected: "150u", actual: chart.YTicks[1].Label);
        Assert.Equal(expected: 150, actual: chart.YTicks[1].Value.Numeric, precision: 6);
        Assert.Equal(expected: 0.5f, actual: chart.YTicks[1].Position, precision: 3);
    }

    [Fact]
    public void Chart_TickStyle_HideGrid_SuppressesGridLines()
    {
        static Chart Build(Func<ChartValue, AxisTickStyle>? style)
        {
            var data = new List<(double X, double Y)> {
                (0, 0),
                (10, 100),
            };
            return LaidOut(
                new Chart {
                    Animated = false,
                    Marks = { LineMark.Of(data: data, x: d => d.X, y: d => d.Y) },
                    YAxis = { TickStyle = style },
                }
            );
        }

        static int PaintCount(Chart chart)
        {
            var paint = new PaintList();
            chart.Paint(paint);
            paint.Validate();
            return paint.Count;
        }

        int full = PaintCount(Build(null));
        int hidden = PaintCount(Build(static _ => new AxisTickStyle { HideGrid = true }));
        Assert.True(
            condition: hidden < full,
            userMessage:
            $"hiding every y grid line should shrink the paint list ({hidden} vs {full})"
        );
    }

    // ── FunctionLineMark ──────────────────────────────────────────────────────

    [Fact]
    public void FunctionLineMark_FeedsDomain_AndPaints()
    {
        var chart = new Chart {
            Animated = false,
            Marks = { new FunctionLineMark(function: Math.Sin, xMin: 0, xMax: 4 * Math.PI) },
        };
        LaidOut(chart);

        // x spans the declared domain; y covers the sampled extent (±1 for sine).
        var x = Assert.IsType<LinearScale>(chart.ResolvedXScale);
        Assert.True(x.DomainMin <= 0 && x.DomainMax >= 4 * Math.PI);
        var y = Assert.IsType<LinearScale>(chart.ResolvedYScale);
        Assert.True(y.DomainMin <= -1 && y.DomainMax >= 1);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 50); // per-pixel sampling strokes plenty of segments
    }

    [Fact]
    public void FunctionLineMark_PoleBreaksTheStroke_NoNanInPaint()
    {
        // 1/x is infinite at 0 — the NaN sample must split the curve, never reach the PaintList
        // (whose NaN validation would throw).
        var chart = new Chart {
            Animated = false,
            Marks = { new FunctionLineMark(function: static x => 1.0 / x, xMin: -5, xMax: 5) },
            YScale = new LinearScale {
                Min = -3,
                Max = 3,
                Nice = false,
            },
        };
        LaidOut(chart);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 20);
    }

    [Fact]
    public void FunctionLineMark_ZoomedIntoPole_KeepsPaintBounded()
    {
        // THE GALLERY CRASH: zoom+pan a dashed 1/x onto its pole. Unclamped samples map millions
        // of px off-plot and the dashed stroke emits one bezier per 8 px of SEGMENT LENGTH —
        // 1.6M+ commands in one frame, blowing past wgpu's 256 MB vertex-buffer cap (native
        // panic). EnsureSamples must clamp screen y to a band around the plot.
        var chart = new Chart {
            Animated = false,
            Marks = {
                new FunctionLineMark(function: static x => 1.0 / x, xMin: -12, xMax: 12) {
                    Dash = 4f,
                },
            },
            YScale = new LinearScale {
                Min = -1.5,
                Max = 1.5,
                Nice = false,
            },
            ScrollableX = true,
            ZoomableX = true,
            VisibleXDomainLength = 12,
            MinVisibleFraction = 0.0001,
        };
        LaidOut(chart: chart, w: 1400, h: 260);

        // ⌘-scroll hard into the left edge, then pan the window onto the pole.
        for (int i = 0; i < 6; i++)
        {
            chart.ZoomBy(factor: 4.0, focus: new Offset(x: chart.PlotRect.X + 2f, y: 130f));
            LaidOut(chart: chart, w: 1400, h: 260);
        }

        chart.ScrollOffsetX = -0.001;
        LaidOut(chart: chart, w: 1400, h: 260);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.InRange(actual: paint.Count, low: 20, high: 50_000);
    }

    // ── Vectorized array factories ────────────────────────────────────────────

    [Fact]
    public void LineMark_VectorizedArrays_PlotDirectly()
    {
        double[] xs = [0, 1, 2, 3];
        double[] ys = [10, 30, 20, 40];

        var paired = new Chart {
            Animated = false,
            Marks = { LineMark.Of(xs: xs, ys: ys) },
        };
        LaidOut(paired);
        Assert.IsType<LinearScale>(paired.ResolvedXScale);
        Assert.True(((LinearScale)paired.ResolvedYScale!).DomainMax >= 40);

        // Index-only overload plots ys against 0..n-1.
        var indexed = new Chart {
            Animated = false,
            Marks = { AreaMark.Of(ys) },
        };
        LaidOut(indexed);
        Assert.True(((LinearScale)indexed.ResolvedXScale!).DomainMax >= 3);

        var paint = new PaintList();
        paired.Paint(paint);
        indexed.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 10);
    }

    // ── Stacking scratch reuse ────────────────────────────────────────────────

    [Fact]
    public void StackCompute_WithScratch_SteadyStateAllocatesZero()
    {
        // A live stacked chart re-resolves (and therefore re-stacks) many times a second; with a
        // caller-owned scratch the per-x column maps are pooled, so the warm path allocates nothing.
        var points = new List<(string Series, ChartValue X, double Value)>();
        for (int x = 0; x < 20; x++)
        {
            points.Add(("a", x, 1 + (x % 3)));
            points.Add(("b", x, 2.0));
            points.Add(("c", x, x % 2 == 0 ? 3.0 : -1.0));
        }

        var order = new List<string> {
            "a",
            "b",
            "c",
        };
        var result = new Dictionary<(string, ChartValue), StackedSpan>();
        var scratch = new StackScratch();

        for (int i = 0; i < 50; i++)
        {
            StackCompute.Compute(
                points: points,
                seriesOrder: order,
                mode: ChartStacking.Normalized,
                result: result,
                scratch: scratch
            );
        }

        const int iterations = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            StackCompute.Compute(
                points: points,
                seriesOrder: order,
                mode: ChartStacking.Normalized,
                result: result,
                scratch: scratch
            );
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: allocated == 0,
            userMessage:
            $"stacked resolve allocated {allocated} B over {iterations} computes; expected 0."
        );
        Assert.Equal(expected: 60, actual: result.Count);
    }

    // ── Native polygon fill (command emission) ────────────────────────────────

    private static int CountKind(PaintList paint, PaintCommandKind kind)
    {
        int c = 0;
        foreach (var cmd in paint.DebugCommands)
        {
            if (cmd.Kind == (byte)kind)
                c++;
        }

        return c;
    }

    [Fact]
    public void PaintList_AddPolygon_EmitsCommand_AndGuardsInput()
    {
        var paint = new PaintList();
        paint.AddPolygon(
            points: [new Offset(x: 0, y: 0), new Offset(x: 10, y: 0), new Offset(x: 5, y: 10)],
            color: Color.Rgb(r: 200, g: 100, b: 50)
        );
        Assert.Equal(
            expected: 1,
            actual: CountKind(paint: paint, kind: PaintCommandKind.Polygon)
        );

        // < 3 points is a no-op.
        paint.AddPolygon(
            points: [new Offset(x: 0, y: 0), new Offset(x: 1, y: 1)],
            color: Color.White
        );
        Assert.Equal(
            expected: 1,
            actual: CountKind(paint: paint, kind: PaintCommandKind.Polygon)
        );

        // NaN is rejected.
        Assert.Throws<ArgumentException>(() =>
            paint.AddPolygon(
                points: [
                    new Offset(x: float.NaN, y: 0), new Offset(x: 1, y: 0),
                    new Offset(x: 0, y: 1),
                ],
                color: Color.White
            )
        );
    }

    [Theory]
    [InlineData(ChartSymbol.Triangle)]
    [InlineData(ChartSymbol.Diamond)]
    public void Chart_FilledSymbols_EmitPolygons(ChartSymbol symbol)
    {
        var data = Enumerable.Range(start: 0, count: 6).Select(i => ((double)i, Math.Sin(i)))
            .ToList();
        var pts = PointMark.Of(data: data, x: d => d.Item1, y: d => d.Item2);
        pts.Symbol = symbol;
        pts.Size = 10f;
        var chart = new Chart {
            Animated = false,
            Marks = { pts },
        };
        LaidOut(chart);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.Equal(
            expected: 6,
            actual: CountKind(paint: paint, kind: PaintCommandKind.Polygon)
        ); // one per point
    }

    [Fact]
    public void Chart_Sectors_FillWithPolygons_PieAndDonut()
    {
        var data = new List<(string, double)> {
            ("A", 40),
            ("B", 35),
            ("C", 25),
        };

        var pie = new Chart {
            Animated = false,
            Marks = { SectorMark.Of(data: data, value: d => d.Item2, category: d => d.Item1) },
        };
        LaidOut(chart: pie, w: 400);
        var piePaint = new PaintList();
        pie.Paint(piePaint);
        piePaint.Validate();
        Assert.True(
            CountKind(paint: piePaint, kind: PaintCommandKind.Polygon) >= 3
        ); // ≥ one fan per slice

        var donutMark = SectorMark.Of(data: data, value: d => d.Item2, category: d => d.Item1);
        donutMark.InnerRadiusFraction = 0.6f;
        var donut = new Chart {
            Animated = false,
            Marks = { donutMark },
        };
        LaidOut(chart: donut, w: 400);
        var donutPaint = new PaintList();
        donut.Paint(donutPaint);
        donutPaint.Validate();
        // Donut wedges tessellate into multiple quads per slice.
        Assert.True(CountKind(paint: donutPaint, kind: PaintCommandKind.Polygon) > 3);
    }

    [Fact]
    public void Chart_AreaPolygonFill_EmitsTrapezoids()
    {
        var data = Enumerable.Range(start: 0, count: 20)
            .Select(i => ((double)i, 5.0 + Math.Sin(i / 2.0)))
            .ToList();
        var area = AreaMark.Of(data: data, x: d => d.Item1, y: d => d.Item2);
        area.UsePolygonFill = true;
        var chart = new Chart {
            Animated = false,
            Marks = { area },
        };
        LaidOut(chart);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(
            CountKind(paint: paint, kind: PaintCommandKind.Polygon) > 5
        ); // trapezoid run
    }

    [Fact]
    public void Chart_AnimatedDataUpdate_MorphsBarGeometry()
    {
        var items = new List<Item> { new() { V = 10 } };
        var bar = BarMark.Of(data: items, x: _ => "A", y: d => d.V);
        bar.CornerRadius = 0f; // plain AddRect so the bar is directly observable
        var chart = new Chart {
            Animated = false,
            LegendPosition = ChartLegendPosition.Hidden,
            YAxis = { ShowGrid = false },
            YScale = new LinearScale {
                Min = 0,
                Max = 20,
                Nice = false,
            },
            Marks = { bar },
        };
        LaidOut(chart);

        float topBefore = BarTop(chart);
        items[0].V = 20;
        chart.InvalidateData(true); // progress 0 → still shows the old height
        LaidOut(chart);
        Assert.Equal(expected: topBefore, actual: BarTop(chart), precision: 1);

        // Drive this chart's animation directly (not the global Ticker.AdvanceAll, which races with
        // xUnit's parallel test classes on shared static state).
        chart.AdvanceAnimation(0.15f); // mid-flight
        float topMid = BarTop(chart);
        chart.AdvanceAnimation(2f); // settle
        float topAfter = BarTop(chart);

        Assert.True(
            condition: topAfter < topBefore,
            userMessage: "bar should end taller (smaller screen y)"
        );
        Assert.True(
            condition: topMid < topBefore && topMid > topAfter,
            userMessage:
            $"mid-animation top {topMid} must sit between {topAfter} and {topBefore}"
        );

        static float BarTop(Chart chart)
        {
            var paint = new PaintList();
            chart.Paint(paint);
            paint.Validate();
            float top = float.MaxValue;
            foreach (var cmd in paint.DebugCommands)
            {
                if (cmd.Kind == (byte)PaintCommandKind.Rect)
                    top = MathF.Min(x: top, y: cmd.RectY);
            }

            Assert.NotEqual(expected: float.MaxValue, actual: top);
            return top;
        }
    }

    [Fact]
    public void Chart_Relayout_IsStable_AfterDataMutation()
    {
        var data = new List<Sale> { new(Month: "Jan", Revenue: 10, Region: "W") };
        var bar = BarMark.Of(data: data, x: d => d.Month, y: d => d.Revenue);
        var chart = new Chart { Marks = { bar } };
        LaidOut(chart);
        int firstTicks = chart.YTicks.Count;

        data.Add(new Sale(Month: "Feb", Revenue: 500, Region: "W"));
        chart.InvalidateData();
        LaidOut(chart); // relayout re-resolves scales through Reset()

        var y = Assert.IsType<LinearScale>(chart.ResolvedYScale);
        Assert.True(y.DomainMax >= 500);
        Assert.True(firstTicks > 0 && chart.YTicks.Count > 0);
        Assert.Equal(expected: 2, actual: chart.XTicks.Count);
    }

    // ── Chart widget, end to end (headless) ──────────────────────────────────

    private sealed record Sale(string Month, double Revenue, string Region);

    // ── Animated data updates ─────────────────────────────────────────────────

    private sealed class Item
    {
        public double V { get; set; }
    }
}
