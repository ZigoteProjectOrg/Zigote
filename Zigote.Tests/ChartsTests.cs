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
        new("Jan", 120, "West"), new("Feb", 180, "West"), new("Mar", 90, "West"),
        new("Jan", 60, "East"), new("Feb", 40, "East"), new("Mar", 150, "East"),
    ];
    // ── NiceScale ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 5, 20)]
    [InlineData(1, 5, 0.2)]
    [InlineData(7, 5, 1)]
    [InlineData(0.55, 5, 0.1)]
    public void NiceScale_TickStep_Uses125Ladder(double range, int target, double expected)
    {
        Assert.Equal(expected, NiceScale.TickStep(range, target), 9);
    }

    [Fact]
    public void NiceScale_NiceDomain_RoundsOutward()
    {
        var (min, max, step) = NiceScale.NiceDomain(0.13, 9.8, 5);
        Assert.Equal(0, min, 9);
        Assert.Equal(10, max, 9);
        Assert.Equal(2, step, 9);
    }

    [Theory]
    [InlineData(1500, "1.5K")]
    [InlineData(2_300_000, "2.3M")]
    [InlineData(950, "950")]
    [InlineData(0.5, "0.5")]
    public void NiceScale_FormatNumber_Compacts(double v, string expected)
    {
        Assert.Equal(expected, NiceScale.FormatNumber(v));
    }

    // ── Scales ────────────────────────────────────────────────────────────────

    [Fact]
    public void LinearScale_NiceDomain_AndNormalize()
    {
        var s = new LinearScale { IncludeZero = true };
        s.Include(3);
        s.Include(97);
        s.FinalizeDomain();

        Assert.Equal(0, s.DomainMin, 6);
        Assert.Equal(100, s.DomainMax, 6);
        Assert.Equal(0.5f, s.NormalizeNumeric(50), 3);

        var ticks = s.BuildTicks(5, null);
        Assert.True(ticks.Count >= 4);
        Assert.All(ticks, t => Assert.InRange(t.Position, -0.001f, 1.001f));
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
        Assert.Equal(-10, s.DomainMin, 6);
        Assert.Equal(10, s.DomainMax, 6);
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

        Assert.Equal(2, s.Categories.Count);
        Assert.Equal(0.25f, s.Normalize("A"), 3);
        Assert.Equal(0.75f, s.Normalize("B"), 3);
        Assert.Equal(0.5f, s.NormalizedBandWidth, 3);

        var wide = new BandScale();
        for (var i = 0; i < 30; i++) wide.Include($"c{i}");
        wide.FinalizeDomain();
        Assert.True(wide.BuildTicks(6, null).Count <= 8); // thinned, not 30 labels
    }

    [Fact]
    public void LogScale_NormalizesDecades()
    {
        var s = new LogScale();
        s.Include(1);
        s.Include(1000);
        s.FinalizeDomain();
        Assert.Equal(0f, s.NormalizeNumeric(1), 3);
        Assert.Equal(1f, s.NormalizeNumeric(1000), 3);
        Assert.Equal(1f / 3f, s.NormalizeNumeric(10), 3);
    }

    [Fact]
    public void TimeScale_PicksCalendarUnits()
    {
        var s = new TimeScale();
        s.Include(new DateTime(2026, 1, 15));
        s.Include(new DateTime(2026, 7, 15));
        s.FinalizeDomain();

        var ticks = s.BuildTicks(6, null);
        Assert.True(ticks.Count is >= 4 and <= 9);
        Assert.Contains(ticks, t => t.Label == "Feb"); // month-aligned labels

        var day = new TimeScale();
        day.Include(
            new DateTime(
                2026,
                3,
                1,
                0,
                0,
                0
            )
        );
        day.Include(
            new DateTime(
                2026,
                3,
                1,
                23,
                59,
                0
            )
        );
        day.FinalizeDomain();
        Assert.Contains(day.BuildTicks(6, null), t => t.Label.Contains(':')); // HH:mm labels
    }

    // ── Stacking ──────────────────────────────────────────────────────────────

    [Fact]
    public void StackCompute_Standard_DivergesAtZero()
    {
        var spans = new Dictionary<(string, ChartValue), StackedSpan>();
        StackCompute.Compute(
            [("a", "x", 3.0), ("b", "x", 2.0), ("c", "x", -4.0)],
            ["a", "b", "c"],
            ChartStacking.Standard,
            spans
        );

        Assert.Equal(new StackedSpan(0, 3), spans[("a", "x")]);
        Assert.Equal(new StackedSpan(3, 5), spans[("b", "x")]);
        Assert.Equal(new StackedSpan(-4, 0), spans[("c", "x")]);
    }

    [Fact]
    public void StackCompute_Normalized_SumsToOne()
    {
        var spans = new Dictionary<(string, ChartValue), StackedSpan>();
        StackCompute.Compute(
            [("a", "x", 1.0), ("b", "x", 3.0)],
            ["a", "b"],
            ChartStacking.Normalized,
            spans
        );

        Assert.Equal(0.25, spans[("a", "x")].Value, 9);
        Assert.Equal(0.75, spans[("b", "x")].Value, 9);
        Assert.Equal(1.0, spans[("b", "x")].Top, 9);
    }

    [Fact]
    public void StackCompute_Center_Silhouette()
    {
        var spans = new Dictionary<(string, ChartValue), StackedSpan>();
        StackCompute.Compute(
            [("a", "x", 2.0), ("b", "x", 2.0)],
            ["a", "b"],
            ChartStacking.Center,
            spans
        );

        Assert.Equal(-2.0, spans[("a", "x")].Bottom, 9);
        Assert.Equal(2.0, spans[("b", "x")].Top, 9);
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    [Fact]
    public void Monotone_NeverOvershoots()
    {
        float[] xs = [0, 1, 2, 3, 4];
        float[] ys = [0, 10, 10, 0, 0];
        var slopes = ChartGeometry.MonotoneSlopes(xs, ys);

        // Flat segments must stay flat (no wiggle past the data range).
        for (var x = 1.0f; x <= 2.0f; x += 0.1f)
            Assert.InRange(
                ChartGeometry.EvaluateMonotone(
                    xs,
                    ys,
                    slopes,
                    x
                ),
                9.999f,
                10.001f
            );
        for (var x = 0.0f; x <= 4.0f; x += 0.05f)
            Assert.InRange(
                ChartGeometry.EvaluateMonotone(
                    xs,
                    ys,
                    slopes,
                    x
                ),
                -0.001f,
                10.001f
            );
    }

    [Fact]
    public void ArcToCubics_EndpointsOnCircle()
    {
        // Quarter arc from 12 o'clock to 3 o'clock around (0,0) r=10.
        var cubics = ChartGeometry.ArcToCubics(
            0,
            0,
            10,
            0,
            MathF.PI / 2f
        );
        Assert.Single(cubics);
        var c = cubics[0];
        Assert.Equal(0f, c.X0, 3);
        Assert.Equal(-10f, c.Y0, 3);
        Assert.Equal(10f, c.X3, 3);
        Assert.Equal(0f, c.Y3, 3);

        // A full half circle splits into two segments.
        Assert.Equal(
            2,
            ChartGeometry.ArcToCubics(
                0,
                0,
                10,
                0,
                MathF.PI
            ).Count
        );
    }

    private static Chart LaidOut(Chart chart, float w = 600, float h = 300)
    {
        chart.Measure(Constraints.Tight(w, h));
        chart.Layout(new Offset(0, 0));
        return chart;
    }

    [Fact]
    public void Chart_ComposesMarks_SharedScales_AndPaints()
    {
        var chart = new Chart {
            Marks = {
                BarMark.Of(Sales, d => d.Month, d => d.Revenue),
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
        Assert.Equal(0, y.DomainMin, 6);
        Assert.True(y.DomainMax >= 200); // stacked column (180+40=220) and the rule fit

        Assert.Equal(3, chart.XTicks.Count);
        Assert.Equal(2, chart.LegendEntries.Count); // West + East

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate(); // balanced clips, no NaN
        Assert.True(paint.Count > 20);
    }

    [Fact]
    public void Chart_Hover_ResolvesColumnCluster_AndTapFires()
    {
        var chart = new Chart {
            Marks = { LineMark.Of(Sales, d => d.Month, d => d.Revenue) },
        };
        ((LineMark<Sale>)chart.Marks[0]).SeriesBy = d => d.Region;
        LaidOut(chart);

        // Hover the middle of the plot: both series report a point at the nearest month.
        var mid = new Offset(
            chart.PlotRect.X + chart.PlotRect.Width / 2f,
            chart.PlotRect.Y + chart.PlotRect.Height / 2f
        );
        chart.OnPointerMove(mid);
        var hover = chart.CurrentHover;
        Assert.NotNull(hover);
        Assert.Equal(2, hover.Points.Count);
        Assert.Equal("Feb", hover.XLabel);

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
        var data = Enumerable.Range(0, 50).Select(i => ((double)i, Math.Sin(i / 5.0))).ToList();
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data, d => d.Item1, d => d.Item2) },
        };
        LaidOut(chart);
        chart.InvalidateData();
        LaidOut(chart);

        var mid = new Offset(
            chart.PlotRect.X + chart.PlotRect.Width / 2f,
            chart.PlotRect.Y + chart.PlotRect.Height / 2f
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
        var data = Enumerable.Range(0, 50).Select(i => ((double)i, Math.Sin(i / 5.0))).ToList();
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data, d => d.Item1, d => d.Item2) },
        };
        LaidOut(chart);

        var mid = new Offset(
            chart.PlotRect.X + chart.PlotRect.Width / 2f,
            chart.PlotRect.Y + chart.PlotRect.Height / 2f
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
        var data = Enumerable.Range(0, 50).Select(i => ((double)i, Math.Sin(i / 5.0))).ToList();
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data, d => d.Item1, d => d.Item2) },
        };
        LaidOut(chart);
        var pins = new List<ChartHoverInfo?>();
        chart.OnPinChanged = p => pins.Add(p);

        var mid = new Offset(
            chart.PlotRect.X + chart.PlotRect.Width / 2f,
            chart.PlotRect.Y + chart.PlotRect.Height / 2f
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
        Assert.Equal(pinnedX, chart.PinnedHover.X);

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
        Assert.Equal(2, pins.Count);
        Assert.Null(pins[1]);

        // Re-pin, then shift the data so the pinned x no longer exists → the pin drops.
        chart.OnPointerMove(mid);
        chart.OnPointerDown(mid);
        chart.OnPointerUp(mid);
        Assert.NotNull(chart.PinnedHover);
        data.Clear();
        data.AddRange(Enumerable.Range(1000, 50).Select(i => ((double)i, Math.Sin(i / 5.0))));
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
        var data = Enumerable.Range(0, 500).Select(i => ((double)i, Math.Sin(i / 15.0)))
            .ToList();
        ChartMark mark = kind == "area"
            ? AreaMark.Of(data, d => d.Item1, d => d.Item2)
            : LineMark.Of(data, d => d.Item1, d => d.Item2);
        var chart = new Chart {
            Animated = false,
            Marks = { mark },
        };
        LaidOut(chart);
        for (var i = 0; i < 50; i++)
        {
            chart.InvalidateData();
            LaidOut(chart);
        }

        const int rounds = 100;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < rounds; i++)
        {
            chart.InvalidateData();
            LaidOut(chart);
        }

        var perRound = (GC.GetAllocatedBytesForCurrentThread() - before) / rounds;
        Assert.True(
            perRound < 8_000,
            $"Resolve path allocated {perRound} B/invalidate; expected well under 8 KB " +
            "(ticks/labels only — no hover-registry rebuild, no fresh resolve collections)."
        );
    }

    [Fact]
    public void Chart_HorizontalBars_WhenYIsCategory()
    {
        var chart = new Chart {
            Marks = { BarMark.Of(Sales, d => d.Revenue, d => d.Month) },
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
        var area = AreaMark.Of(Sales, d => d.Month, d => d.Revenue);
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
            Marks = { SectorMark.Of(data, d => d.Value, d => d.Name) },
        };
        LaidOut(chart, 400);

        Assert.Empty(chart.XTicks); // polar-only chart hides the cartesian axes
        Assert.Equal(3, chart.LegendEntries.Count);

        // 'A' spans the first half of the sweep; just right of 12 o'clock lands inside it.
        var cx = chart.PlotRect.X + chart.PlotRect.Width / 2f;
        var cy = chart.PlotRect.Y + chart.PlotRect.Height / 2f;
        var r = MathF.Min(chart.PlotRect.Width, chart.PlotRect.Height) / 2f * 0.7f;
        chart.OnPointerMove(new Offset(cx + r * 0.5f, cy - r * 0.5f));
        var hover = chart.CurrentHover;
        Assert.NotNull(hover);
        Assert.Equal("A", hover.XLabel);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(paint.Count > 10);
    }

    [Fact]
    public void Chart_TimeSeries_PointsAndLog()
    {
        var start = new DateTime(2026, 1, 1);
        var series = Enumerable.Range(0, 90)
            .Select(i => (Day: start.AddDays(i), Value: Math.Pow(10, 1 + i / 45.0)))
            .ToList();

        var chart = new Chart {
            YScale = new LogScale(),
            Marks = {
                LineMark.Of(series, d => d.Day, d => d.Value),
                PointMark.Of(series, d => d.Day, d => d.Value),
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
        Assert.Equal((0.0, 100.0), s.FullExtent);

        s.SetVisibleWindow(20, 40);
        Assert.Equal(0.5f, s.NormalizeNumeric(30), 3);
        Assert.All(s.BuildTicks(5, null), t => Assert.InRange(t.Position, -0.001f, 1.001f));
    }

    [Fact]
    public void BandScale_VisibleWindow_ShowsIndexRange()
    {
        var s = new BandScale();
        for (var i = 0; i < 10; i++) s.Include($"c{i}");
        s.FinalizeDomain();

        s.SetVisibleWindow(2, 6); // categories 2..5 visible
        Assert.Equal(0.25f, s.NormalizedBandWidth, 3);
        Assert.Equal(0.375f, s.Normalize("c3"), 3); // (3.5 - 2) / 4
        Assert.All(s.BuildTicks(10, null), t => Assert.InRange(t.Position, -0.03f, 1.03f));
    }

    [Fact]
    public void Chart_ScrollableX_StartsAtEnd_PansAndClamps()
    {
        var data = Enumerable.Range(0, 100).Select(i => ((double)i, Math.Sin(i / 5.0)))
            .ToList();
        var chart = new Chart {
            Animated = false,
            ScrollableX = true,
            VisibleXDomainLength = 20.0,
            Marks = { LineMark.Of(data, d => d.Item1, d => d.Item2) },
        };
        LaidOut(chart);

        // Nice domain of 0..99 is 0..100 → the initial window sticks to the newest data.
        Assert.Equal(80.0, chart.ScrollOffsetX, 2);

        chart.ScrollOffsetX = 30;
        LaidOut(chart);
        var x = Assert.IsType<LinearScale>(chart.ResolvedXScale);
        Assert.Equal(0.5f, x.NormalizeNumeric(40), 3);

        // Drag right by 100px → window pans toward earlier data, tap suppressed.
        ChartHoverInfo? tapped = null;
        chart.OnPointTap = i => tapped = i;
        var mid = new Offset(
            chart.PlotRect.X + chart.PlotRect.Width / 2f,
            chart.PlotRect.Y + chart.PlotRect.Height / 2f
        );
        chart.OnPointerDown(mid);
        chart.OnPointerMove(new Offset(mid.X + 100f, mid.Y));
        chart.OnPointerUp(new Offset(mid.X + 100f, mid.Y));

        var expected = 30.0 - 100.0 * 20.0 / chart.PlotRect.Width;
        Assert.Equal(expected, chart.ScrollOffsetX, 2);
        Assert.Null(tapped);
        Assert.Null(chart.CurrentHover);

        // Clamp at the front edge.
        chart.ScrollOffsetX = -500;
        LaidOut(chart);
        Assert.Equal(0.0, chart.ScrollOffsetX, 2);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate(); // includes the scroll indicator
    }

    [Fact]
    public void Chart_ScrollableY_WindowsValueAxis_AndPansWithVerticalDrag()
    {
        var data = Enumerable.Range(0, 100).Select(i => ((double)i, (double)i)).ToList();
        var chart = new Chart {
            Animated = false,
            ScrollableY = true,
            VisibleYDomainLength = 20.0,
            Marks = { LineMark.Of(data, d => d.Item1, d => d.Item2) },
        };
        LaidOut(chart);

        // Y is not stick-to-end → starts at the bottom of the (nice) domain.
        Assert.Equal(0.0, chart.ScrollOffsetY, 2);
        var y = Assert.IsType<LinearScale>(chart.ResolvedYScale);
        Assert.Equal(0f, y.NormalizeNumeric(0), 3);
        Assert.Equal(1f, y.NormalizeNumeric(20), 3);

        // Dragging down reveals higher values (offset increases).
        var mid = new Offset(
            chart.PlotRect.X + chart.PlotRect.Width / 2f,
            chart.PlotRect.Y + chart.PlotRect.Height / 2f
        );
        chart.OnPointerDown(mid);
        chart.OnPointerMove(new Offset(mid.X, mid.Y + chart.PlotRect.Height / 2f));
        chart.OnPointerUp(new Offset(mid.X, mid.Y + chart.PlotRect.Height / 2f));
        Assert.True(chart.ScrollOffsetY > 0.0);
    }

    [Fact]
    public void Chart_ZoomBy_ShrinksWindow_KeepingFocusPoint()
    {
        var data = Enumerable.Range(0, 100).Select(i => ((double)i, Math.Sin(i / 5.0)))
            .ToList();
        var chart = new Chart {
            Animated = false,
            ZoomableX = true,
            Marks = { LineMark.Of(data, d => d.Item1, d => d.Item2) },
        };
        LaidOut(chart);
        Assert.IsType<LinearScale>(chart.ResolvedXScale);

        // Zoom 2× around the plot centre (domain ~50 for a 0..100 nice domain).
        var centre = new Offset(
            chart.PlotRect.X + chart.PlotRect.Width / 2f,
            chart.PlotRect.Y + 10f
        );
        chart.ZoomBy(2.0, centre);
        LaidOut(chart);
        var x = Assert.IsType<LinearScale>(chart.ResolvedXScale);
        // The visible window halved (0..100 → ~25..75), so 50 stays centred.
        Assert.Equal(0.5f, x.NormalizeNumeric(50), 2);
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
        var data = Enumerable.Range(0, 50).Select(i => ((double)i, (double)i)).ToList();
        var chart = new Chart {
            Animated = false,
            EnableXSelection = true,
            Marks = { LineMark.Of(data, d => d.Item1, d => d.Item2) },
        };
        LaidOut(chart);

        (double Min, double Max)? reported = null;
        chart.OnXRangeSelected = r => reported = r;

        var plot = chart.PlotRect;
        var x0 = plot.X + plot.Width * 0.25f;
        var x1 = plot.X + plot.Width * 0.75f;
        var y = plot.Y + plot.Height / 2f;
        chart.OnPointerDown(new Offset(x0, y));
        chart.OnPointerMove(new Offset(x1, y));
        chart.OnPointerUp(new Offset(x1, y));

        Assert.NotNull(reported);
        Assert.NotNull(chart.SelectedXRange);
        // Nice domain 0..49 → 0..50; the quarter/three-quarter marks map to ~12.5 and ~37.5.
        Assert.InRange(chart.SelectedXRange!.Value.Min, 10.0, 15.0);
        Assert.InRange(chart.SelectedXRange!.Value.Max, 35.0, 40.0);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate(); // selection band drawn

        // A click (no drag) clears it.
        chart.OnPointerDown(new Offset(x0, y));
        chart.OnPointerUp(new Offset(x0 + 1f, y));
        Assert.Null(chart.SelectedXRange);
        Assert.Null(reported);
    }

    // ── Dual y-axes ───────────────────────────────────────────────────────────

    [Fact]
    public void Chart_DualYAxes_IndependentScales_SharedSeriesColors()
    {
        var priceData = Enumerable.Range(0, 12).Select(i => ((double)i, 100.0 + i)).ToList();
        var volData = Enumerable.Range(0, 12).Select(i => ((double)i, (double)(i * 1000)))
            .ToList();

        var price = LineMark.Of(priceData, d => d.Item1, d => d.Item2);
        price.Name = "price";
        var vol = BarMark.Of(volData, d => d.Item1, d => d.Item2);
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
        var data = Enumerable.Range(0, 10).Select(i => ((double)i, (double)i)).ToList();
        var chart = new Chart {
            Animated = false,
            Marks = { LineMark.Of(data, d => d.Item1, d => d.Item2) },
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
        Assert.Contains(paint.DebugCommands, c => c.Kind == (byte)PaintCommandKind.Text);
    }

    // ── Decimation / LOD ──────────────────────────────────────────────────────

    [Fact]
    public void Lttb_PreservesEndpointsAndExtremes()
    {
        var n = 1000;
        var xs = new float[n];
        var ys = new float[n];
        for (var i = 0; i < n; i++)
        {
            xs[i] = i;
            ys[i] = MathF.Sin(i / 20f);
        }

        ys[500] = 99f; // a spike the decimation must keep

        var idx = ChartGeometry.LttbIndices(xs, ys, 50);
        Assert.NotNull(idx);
        Assert.Equal(50, idx!.Length);
        Assert.Equal(0, idx[0]);
        Assert.Equal(n - 1, idx[^1]);
        Assert.Contains(500, idx); // the spike survived
        // Indices strictly ascending.
        for (var i = 1; i < idx.Length; i++) Assert.True(idx[i] > idx[i - 1]);

        // Already-small series pass through untouched.
        Assert.Null(ChartGeometry.LttbIndices(xs.AsSpan(0, 10), ys.AsSpan(0, 10), 50));
    }

    [Fact]
    public void Chart_LineMark_MaxRenderPoints_CutsStrokeCommands()
    {
        var data = Enumerable.Range(0, 2000).Select(i => ((double)i, Math.Sin(i / 30.0)))
            .ToList();

        int PaintCommandCount(int cap)
        {
            var line = LineMark.Of(data, d => d.Item1, d => d.Item2);
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

        var full = PaintCommandCount(0);
        var capped = PaintCommandCount(100);
        Assert.True(
            capped < full / 2,
            $"decimated ({capped}) should be far below full ({full})"
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
        var data = Enumerable.Range(0, 200).Select(i => ((double)i, Math.Sin(i / 15.0)))
            .ToList();
        var linear = LineMark.Of(data, d => d.Item1, d => d.Item2);
        linear.Interpolation = ChartInterpolation.Linear;
        ChartMark mark = kind switch {
            "monotone-line" => LineMark.Of(data, d => d.Item1, d => d.Item2),
            "linear-line" => linear,
            "area" => AreaMark.Of(data, d => d.Item1, d => d.Item2),
            "bars" => BarMark.Of(data.Take(12).ToList(), d => d.Item1, d => d.Item2),
            "function" => new FunctionLineMark(static x => Math.Sin(x / 15.0), 0, 200),
            _ => PointMark.Of(data, d => d.Item1, d => d.Item2),
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
            chart.OverlayPainter = static (p, proxy) => p.AddRect(
                new Rect(
                    proxy.PlotRect.X,
                    proxy.PositionY(0.5),
                    proxy.PlotRect.Width,
                    2f
                ),
                Color.Rgb(52, 199, 89)
            );

        LaidOut(chart);
        var paint = new PaintList();

        // Warm up past tiered JIT and populate the Utf8 / TextMeasure / PaintList-capacity caches.
        for (var i = 0; i < 200; i++)
        {
            paint.Clear();
            chart.Paint(paint);
        }

        Assert.True(paint.Count > 10);

        const int frames = 300;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++)
        {
            paint.Clear();
            chart.Paint(paint);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(
            allocated == 0,
            $"{kind} chart paint allocated {allocated} B over {frames} frames " +
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
        Assert.Equal(20, linear.NumericAt(linear.NormalizeNumeric(20)), 9);
        Assert.Equal(10, linear.NumericAt(0f), 9);
        Assert.Equal(30, linear.NumericAt(1f), 9);

        var log = new LogScale {
            Min = 1,
            Max = 1000,
        };
        log.FinalizeDomain();
        // Round trip through a float normalize + pow10 — compare at float precision.
        Assert.Equal(100, log.NumericAt(log.NormalizeNumeric(100)), 2);

        var band = new BandScale();
        band.Include("A");
        band.Include("B");
        band.Include("C");
        band.FinalizeDomain();
        // Band 1's centre normalizes to 0.5 → NumericAt gives back the band-index magnitude 1.5.
        Assert.Equal(1.5, band.NumericAt(band.Normalize(ChartValue.Category("B"))), 6);
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
            Marks = { LineMark.Of(data, d => d.X, d => d.Y) },
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
        Assert.Equal(chart.PlotRect.X + chart.PlotRect.Width / 2f, proxy.PositionX(5), 2);
        Assert.Equal(chart.PlotRect.Bottom, proxy.PositionY(0), 2);
        Assert.Equal(chart.PlotRect.Y, proxy.PositionY(100), 2);

        // Screen → domain inverts the projection.
        Assert.Equal(5, proxy.XValueAt(proxy.PositionX(5)), 4);
        Assert.Equal(42, proxy.YValueAt(proxy.PositionY(42)), 3);

        var p = proxy.Position(5, 50);
        Assert.Equal(proxy.PositionX(5), p.X, 3);
        Assert.Equal(proxy.PositionY(50), p.Y, 3);
    }

    // ── Axis customization (TickValues + TickStyle) ───────────────────────────

    [Fact]
    public void Chart_CustomTickValues_PinTheAxisTicks()
    {
        var chart = new Chart {
            Animated = false,
            Marks = { BarMark.Of(Sales, d => d.Month, d => d.Revenue) },
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

        Assert.Equal(3, chart.YTicks.Count);
        Assert.Equal("150u", chart.YTicks[1].Label);
        Assert.Equal(150, chart.YTicks[1].Value.Numeric, 6);
        Assert.Equal(0.5f, chart.YTicks[1].Position, 3);
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
                    Marks = { LineMark.Of(data, d => d.X, d => d.Y) },
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

        var full = PaintCount(Build(null));
        var hidden = PaintCount(Build(static _ => new AxisTickStyle { HideGrid = true }));
        Assert.True(
            hidden < full,
            $"hiding every y grid line should shrink the paint list ({hidden} vs {full})"
        );
    }

    // ── FunctionLineMark ──────────────────────────────────────────────────────

    [Fact]
    public void FunctionLineMark_FeedsDomain_AndPaints()
    {
        var chart = new Chart {
            Animated = false,
            Marks = { new FunctionLineMark(Math.Sin, 0, 4 * Math.PI) },
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
            Marks = { new FunctionLineMark(static x => 1.0 / x, -5, 5) },
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
                new FunctionLineMark(static x => 1.0 / x, -12, 12) { Dash = 4f },
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
        LaidOut(chart, 1400, 260);

        // ⌘-scroll hard into the left edge, then pan the window onto the pole.
        for (var i = 0; i < 6; i++)
        {
            chart.ZoomBy(4.0, new Offset(chart.PlotRect.X + 2f, 130f));
            LaidOut(chart, 1400, 260);
        }

        chart.ScrollOffsetX = -0.001;
        LaidOut(chart, 1400, 260);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.InRange(paint.Count, 20, 50_000);
    }

    // ── Vectorized array factories ────────────────────────────────────────────

    [Fact]
    public void LineMark_VectorizedArrays_PlotDirectly()
    {
        double[] xs = [0, 1, 2, 3];
        double[] ys = [10, 30, 20, 40];

        var paired = new Chart {
            Animated = false,
            Marks = { LineMark.Of(xs, ys) },
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
        for (var x = 0; x < 20; x++)
        {
            points.Add(("a", x, 1 + x % 3));
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

        for (var i = 0; i < 50; i++)
            StackCompute.Compute(
                points,
                order,
                ChartStacking.Normalized,
                result,
                scratch
            );

        const int iterations = 200;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            StackCompute.Compute(
                points,
                order,
                ChartStacking.Normalized,
                result,
                scratch
            );
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"stacked resolve allocated {allocated} B over {iterations} computes; expected 0."
        );
        Assert.Equal(60, result.Count);
    }

    // ── Native polygon fill (command emission) ────────────────────────────────

    private static int CountKind(PaintList paint, PaintCommandKind kind)
    {
        var c = 0;
        foreach (var cmd in paint.DebugCommands)
            if (cmd.Kind == (byte)kind)
                c++;
        return c;
    }

    [Fact]
    public void PaintList_AddPolygon_EmitsCommand_AndGuardsInput()
    {
        var paint = new PaintList();
        paint.AddPolygon(
            [new Offset(0, 0), new Offset(10, 0), new Offset(5, 10)],
            Color.Rgb(200, 100, 50)
        );
        Assert.Equal(1, CountKind(paint, PaintCommandKind.Polygon));

        // < 3 points is a no-op.
        paint.AddPolygon([new Offset(0, 0), new Offset(1, 1)], Color.White);
        Assert.Equal(1, CountKind(paint, PaintCommandKind.Polygon));

        // NaN is rejected.
        Assert.Throws<ArgumentException>(() =>
            paint.AddPolygon(
                [new Offset(float.NaN, 0), new Offset(1, 0), new Offset(0, 1)],
                Color.White
            )
        );
    }

    [Theory]
    [InlineData(ChartSymbol.Triangle)]
    [InlineData(ChartSymbol.Diamond)]
    public void Chart_FilledSymbols_EmitPolygons(ChartSymbol symbol)
    {
        var data = Enumerable.Range(0, 6).Select(i => ((double)i, Math.Sin(i))).ToList();
        var pts = PointMark.Of(data, d => d.Item1, d => d.Item2);
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
        Assert.Equal(6, CountKind(paint, PaintCommandKind.Polygon)); // one per point
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
            Marks = { SectorMark.Of(data, d => d.Item2, d => d.Item1) },
        };
        LaidOut(pie, 400);
        var piePaint = new PaintList();
        pie.Paint(piePaint);
        piePaint.Validate();
        Assert.True(CountKind(piePaint, PaintCommandKind.Polygon) >= 3); // ≥ one fan per slice

        var donutMark = SectorMark.Of(data, d => d.Item2, d => d.Item1);
        donutMark.InnerRadiusFraction = 0.6f;
        var donut = new Chart {
            Animated = false,
            Marks = { donutMark },
        };
        LaidOut(donut, 400);
        var donutPaint = new PaintList();
        donut.Paint(donutPaint);
        donutPaint.Validate();
        // Donut wedges tessellate into multiple quads per slice.
        Assert.True(CountKind(donutPaint, PaintCommandKind.Polygon) > 3);
    }

    [Fact]
    public void Chart_AreaPolygonFill_EmitsTrapezoids()
    {
        var data = Enumerable.Range(0, 20).Select(i => ((double)i, 5.0 + Math.Sin(i / 2.0)))
            .ToList();
        var area = AreaMark.Of(data, d => d.Item1, d => d.Item2);
        area.UsePolygonFill = true;
        var chart = new Chart {
            Animated = false,
            Marks = { area },
        };
        LaidOut(chart);

        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        Assert.True(CountKind(paint, PaintCommandKind.Polygon) > 5); // trapezoid run
    }

    [Fact]
    public void Chart_AnimatedDataUpdate_MorphsBarGeometry()
    {
        var items = new List<Item> { new() { V = 10 } };
        var bar = BarMark.Of(items, _ => "A", d => d.V);
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

        var topBefore = BarTop(chart);
        items[0].V = 20;
        chart.InvalidateData(true); // progress 0 → still shows the old height
        LaidOut(chart);
        Assert.Equal(topBefore, BarTop(chart), 1);

        // Drive this chart's animation directly (not the global Ticker.AdvanceAll, which races with
        // xUnit's parallel test classes on shared static state).
        chart.AdvanceAnimation(0.15f); // mid-flight
        var topMid = BarTop(chart);
        chart.AdvanceAnimation(2f); // settle
        var topAfter = BarTop(chart);

        Assert.True(topAfter < topBefore, "bar should end taller (smaller screen y)");
        Assert.True(
            topMid < topBefore && topMid > topAfter,
            $"mid-animation top {topMid} must sit between {topAfter} and {topBefore}"
        );

        static float BarTop(Chart chart)
        {
            var paint = new PaintList();
            chart.Paint(paint);
            paint.Validate();
            var top = float.MaxValue;
            foreach (var cmd in paint.DebugCommands)
                if (cmd.Kind == (byte)PaintCommandKind.Rect)
                    top = MathF.Min(top, cmd.RectY);
            Assert.NotEqual(float.MaxValue, top);
            return top;
        }
    }

    [Fact]
    public void Chart_Relayout_IsStable_AfterDataMutation()
    {
        var data = new List<Sale> { new("Jan", 10, "W") };
        var bar = BarMark.Of(data, d => d.Month, d => d.Revenue);
        var chart = new Chart { Marks = { bar } };
        LaidOut(chart);
        var firstTicks = chart.YTicks.Count;

        data.Add(new Sale("Feb", 500, "W"));
        chart.InvalidateData();
        LaidOut(chart); // relayout re-resolves scales through Reset()

        var y = Assert.IsType<LinearScale>(chart.ResolvedYScale);
        Assert.True(y.DomainMax >= 500);
        Assert.True(firstTicks > 0 && chart.YTicks.Count > 0);
        Assert.Equal(2, chart.XTicks.Count);
    }

    // ── Chart widget, end to end (headless) ──────────────────────────────────

    private sealed record Sale(string Month, double Revenue, string Region);

    // ── Animated data updates ─────────────────────────────────────────────────

    private sealed class Item
    {
        public double V { get; set; }
    }
}