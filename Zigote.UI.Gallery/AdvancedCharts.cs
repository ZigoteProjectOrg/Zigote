using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Charts.Scales;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Gallery;

/// <summary>
///     A self-animating, real-time line chart. Appends a fresh sample every ~16 ms via a
///     <see cref="Ticker" />
///     (so the app keeps drawing while it's on screen), trims points older than the visible window,
///     and keeps
///     the view pinned to the newest data. Demonstrates streaming data + a moving scroll window.
/// </summary>
internal sealed class LiveLineChart : Widget
{
    private const double WindowSeconds = 12.0;
    private const double SampleInterval = 0.06; // ~16 Hz

    private readonly Chart _chart;
    private readonly List<Sample> _cpu = [];
    private readonly List<Sample> _gpu = [];
    private readonly Random _rng = new(7);

    private double _cpuLevel = 32;
    private double _gpuLevel = 58;
    private double _sinceSample;
    private Size _size;
    private double _t;
    private Ticker? _ticker;

    public LiveLineChart()
    {
        // Seed a full window of history so the chart opens mid-stream rather than empty.
        for (var seed = -WindowSeconds; seed < 0; seed += SampleInterval)
        {
            _t = seed;
            Emit();
        }

        _t = 0;

        var cpu = LineMark.Of(_cpu, s => s.T, s => s.V);
        cpu.Name = "CPU";
        cpu.Interpolation = ChartInterpolation.Monotone;
        cpu.Color = Color.Rgb(10, 132, 255);

        var gpu = LineMark.Of(_gpu, s => s.T, s => s.V);
        gpu.Name = "GPU";
        gpu.Interpolation = ChartInterpolation.Monotone;
        gpu.Color = Color.Rgb(255, 149, 0);

        _chart = new Chart {
            Marks = {
                cpu,
                gpu,
            },
            Animated = false, // streaming — no entrance sweep
            AnimateDataUpdates = false, // pushed too often to morph
            ScrollableX = true,
            VisibleXDomainLength = WindowSeconds,
            ShowScrollIndicator = false,
            YScale = new LinearScale {
                Min = 0,
                Max = 100,
                Nice = false,
            },
            YAxis = { Title = "%" },
            XAxis = { ShowLabels = false }, // relative time — tick labels aren't meaningful
        };
    }

    private void Emit()
    {
        // Smooth clamped random walk with a slow sinusoidal bias so the two series stay lively but bounded.
        _cpuLevel = Math.Clamp(
            _cpuLevel + (_rng.NextDouble() - 0.5) * 9 + Math.Sin(_t * 0.7) * 1.4,
            4,
            96
        );
        _gpuLevel = Math.Clamp(
            _gpuLevel + (_rng.NextDouble() - 0.5) * 7 + Math.Sin(_t * 0.4 + 1) * 1.1,
            4,
            96
        );
        _cpu.Add(new Sample(_t, _cpuLevel));
        _gpu.Add(new Sample(_t, _gpuLevel));
    }

    private void OnTick(float dt)
    {
        _t += dt;
        _sinceSample += dt;

        var changed = false;
        while (_sinceSample >= SampleInterval)
        {
            _sinceSample -= SampleInterval;
            Emit();
            changed = true;
        }

        if (!changed) return;

        // Bound memory: drop samples that have scrolled off the left edge (+ a little slack).
        var cutoff = _t - WindowSeconds - 1.0;
        _cpu.RemoveAll(s => s.T < cutoff);
        _gpu.RemoveAll(s => s.T < cutoff);

        _chart.InvalidateData();
        _chart.ScrollToEnd();
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        _chart.Attach(owner, this);
        _ticker ??= new Ticker(OnTick);
        _ticker.Start();
    }

    public override void Detach()
    {
        _ticker?.Dispose();
        _ticker = null;
        base.Detach();
    }

    public override Size Measure(Constraints c)
    {
        _size = _chart.Measure(c);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _chart.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _chart.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return _chart.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_chart];
    }

    private readonly record struct Sample(double T, double V);
}

/// <summary>Builders for the advanced/interactive chart demos shown in the gallery's Charts tab.</summary>
internal static class DemoCharts
{
    private static Widget Box(Chart chart, float height = 240f)
    {
        return new SizedBox(height: height, child: chart);
    }

    // ── Scroll + zoom over a dense series ────────────────────────────────────────
    public static Widget ZoomPan()
    {
        var rng = new Random(11);
        var data = new List<Pt>();
        double price = 120;
        for (var i = 0; i < 220; i++)
        {
            price = Math.Max(20, price + (rng.NextDouble() - 0.48) * 9);
            data.Add(new Pt(i, price));
        }

        var line = LineMark.Of(data, d => d.X, d => d.Y);
        line.Interpolation = ChartInterpolation.Monotone;
        line.Color = Color.Rgb(52, 199, 89);

        var chart = new Chart {
            Marks = { line },
            ScrollableX = true,
            ZoomableX = true,
            VisibleXDomainLength = 45, // open showing ~45 of the 220 points
            XAxis = { Title = "day" },
            YAxis = { Title = "price" },
        };
        chart.ScrollToEnd();

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children: [
                Box(chart, 250f),
                new SizedBox(height: 8),
                new Row(
                    [
                        new Text(
                            "Drag to pan · ⌘/Ctrl-scroll to zoom",
                            new TextStyle(12, color: Colors.Grey[500])
                        ),
                        new Spacer(),
                        new OutlinedButton(
                            new Text("Reset zoom"),
                            () =>
                            {
                                chart.ResetZoom();
                                chart.ScrollToEnd();
                            }
                        ),
                    ]
                ),
            ]
        );
    }

    // ── Drag-to-select an x-range, reporting the interval + mean ──────────────────
    public static Widget RangeSelection()
    {
        var rng = new Random(3);
        var data = new List<Pt>();
        double v = 50;
        for (var i = 0; i < 60; i++)
        {
            v = Math.Clamp(v + (rng.NextDouble() - 0.5) * 16, 8, 92);
            data.Add(new Pt(i, v));
        }

        var area = AreaMark.Of(data, d => d.X, d => d.Y);
        area.Interpolation = ChartInterpolation.Monotone;
        area.Color = Color.Rgb(88, 86, 214);

        var readout = new Text(
            "Drag across the plot to select a range",
            new TextStyle(12, color: Colors.Grey[500])
        );

        var chart = new Chart {
            Marks = { area },
            EnableXSelection = true,
            XAxis = { Title = "sample" },
            YAxis = { Title = "value" },
            OnXRangeSelected = range =>
            {
                if (range is not { } r)
                {
                    readout.Text = "Drag across the plot to select a range";
                    return;
                }

                var lo = (int)Math.Round(r.Min);
                var hi = (int)Math.Round(r.Max);
                var slice = data.Where(d => d.X >= lo && d.X <= hi).ToList();
                var mean = slice.Count > 0 ? slice.Average(d => d.Y) : 0;
                readout.Text = $"Selected {lo}–{hi}  ·  {slice.Count} points  ·  mean {mean:F1}";
            },
        };

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children: [
                Box(chart),
                new SizedBox(height: 8),
                readout,
            ]
        );
    }

    // ── Dual y-axes: a price line on the left, volume bars on the right ───────────
    public static Widget DualAxis()
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug"];
        var rng = new Random(21);
        var rows = new List<Bar>();
        double price = 180;
        for (var i = 0; i < months.Length; i++)
        {
            price += (rng.NextDouble() - 0.4) * 40;
            rows.Add(new Bar(months[i], price, 20 + rng.NextDouble() * 80));
        }

        var volume = BarMark.Of(rows, d => d.M, d => d.Vol);
        volume.Name = "Volume";
        volume.UseSecondaryYAxis = true;
        volume.CornerRadius = 3f;
        volume.Color = Color.Rgba(
            120,
            128,
            140,
            0.55f
        );

        var priceLine = LineMark.Of(rows, d => d.M, d => d.Price);
        priceLine.Name = "Price";
        priceLine.Interpolation = ChartInterpolation.Monotone;
        priceLine.ShowSymbols = true;
        priceLine.Color = Color.Rgb(255, 59, 48);

        var chart = new Chart {
            Marks = {
                volume,
                priceLine,
            }, // bars first so the line paints on top
            YAxis = { Title = "price $" },
            YAxis2 = { Title = "volume" },
        };

        return Box(chart, 250f);
    }

    // ── Heatmap: activity by weekday × hour band, colour = magnitude ─────────────
    public static Widget Heatmap()
    {
        string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        string[] hours = ["0", "3", "6", "9", "12", "15", "18", "21"];
        var rng = new Random(5);
        var cells = new List<Cell>();
        for (var d = 0; d < days.Length; d++)
        for (var h = 0; h < hours.Length; h++)
        {
            // Weekday work hours peak; weekends flatter.
            var work = (h is >= 3 and <= 5 ? 1.0 : 0.35) * (d < 5 ? 1.0 : 0.5);
            cells.Add(new Cell(days[d], hours[h], work * (0.5 + rng.NextDouble()) * 100));
        }

        var heat = RectangleMark.Of(cells, c => c.Day, c => c.Hour);
        heat.FillBy = c => c.V;
        heat.LowColor = Color.Rgba(
            10,
            132,
            255,
            0.10f
        );
        heat.HighColor = Color.Rgb(10, 132, 255);
        heat.CornerRadius = 3f;

        var chart = new Chart {
            Marks = { heat },
            LegendPosition = ChartLegendPosition.Hidden,
            XAxis = { Title = "weekday" },
            YAxis = { Title = "hour" },
        };

        return Box(chart, 220f);
    }

    // ── Composed marks: bars + a target threshold rule + a trend line + annotation ─
    public static Widget ThresholdTrend()
    {
        string[] q = ["W1", "W2", "W3", "W4", "W5", "W6", "W7", "W8"];
        double[] vals = [42, 58, 51, 73, 66, 91, 78, 84];
        var rows = new List<Bar2>();
        for (var i = 0; i < q.Length; i++) rows.Add(new Bar2(q[i], vals[i]));

        // Peak marker.
        var peakIdx = 0;
        for (var i = 1; i < vals.Length; i++)
            if (vals[i] > vals[peakIdx])
                peakIdx = i;

        var bars = BarMark.Of(rows, d => d.M, d => d.V);
        bars.Name = "Weekly";
        bars.Color = Color.Rgba(
            10,
            132,
            255,
            0.85f
        );

        var trend = LineMark.Of(rows, d => d.M, d => d.V);
        trend.Name = "Trend";
        trend.Interpolation = ChartInterpolation.Monotone;
        trend.Color = Color.Rgb(255, 149, 0);

        var target = new RuleMark {
            Y = 80,
            Label = "target",
        };

        var chart = new Chart {
            Marks = {
                bars,
                trend,
                target,
            },
            YAxis = { Title = "units" },
        };
        chart.Annotations.Add(
            new ChartAnnotation {
                X = q[peakIdx],
                Y = vals[peakIdx],
                Text = "peak",
                Placement = ChartAnnotationPlacement.Above,
            }
        );

        return Box(chart, 220f);
    }

    // ── Normalized (100%) stacked bars: each column rescaled to its total ────────
    public static Widget NormalizedStack()
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun"];
        string[] platforms = ["Desktop", "Mobile", "Web"];
        var rng = new Random(17);
        var rows = new List<Share>();
        foreach (var m in months)
        foreach (var p in platforms)
            rows.Add(new Share(m, p, 20 + rng.NextDouble() * 80));

        var bars = BarMark.Of(
            rows,
            d => d.Month,
            d => d.V,
            d => d.Platform
        );
        bars.Stacking = ChartStacking.Normalized;

        var chart = new Chart {
            Marks = { bars },
            YAxis = {
                // Pinned quarter ticks + percent labels — the normalized domain is [0, 1].
                TickValues = [0.0, 0.25, 0.5, 0.75, 1.0],
                Formatter = v => $"{v.Numeric * 100:F0}%",
            },
        };

        return Box(chart, 220f);
    }

    // ── Streamgraph: center-stacked areas, silhouette around zero ────────────────
    public static Widget Streamgraph()
    {
        string[] genres = ["Action", "Puzzle", "Racing", "Sim"];
        var rows = new List<Flow>();
        for (var i = 0; i <= 30; i++)
        for (var g = 0; g < genres.Length; g++)
        {
            var v = 12 + 10 * Math.Sin(i / 4.5 + g * 1.7) + 6 * Math.Sin(i / 2.1 + g) + g * 3;
            rows.Add(new Flow(i, genres[g], Math.Max(1, v)));
        }

        var stream = AreaMark.Of(
            rows,
            d => d.T,
            d => d.V,
            d => d.Genre
        );
        stream.Stacking = ChartStacking.Center;
        stream.Opacity = 0.75f;
        stream.StrokeTop = false;
        stream.UsePolygonFill = true;

        var chart = new Chart {
            Marks = { stream },
            YAxis = { Show = false }, // the silhouette is the point — no value axis
            XAxis = { Title = "week" },
        };

        return Box(chart, 220f);
    }

    // ── Function plots: y = f(x) sampled per pixel, poles break the stroke ───────
    public static Widget FunctionPlot()
    {
        var sinc = new FunctionLineMark(x => x == 0 ? 1.0 : Math.Sin(x * 2) / (x * 2), -12, 12) {
            Name = "sinc 2x",
            Color = Color.Rgb(10, 132, 255),
        };
        var damped = new FunctionLineMark(
            x => Math.Cos(3 * x) * Math.Exp(-Math.Abs(x) / 6.0),
            -12,
            12
        ) {
            Name = "cos 3x · e^(-|x|/6)",
            Color = Color.Rgb(255, 149, 0),
        };
        var pole = new FunctionLineMark(x => 1.0 / x, -12, 12) {
            Name = "1/x",
            Color = Color.Rgb(255, 59, 48),
            Dash = 4f,
        };

        var chart = new Chart {
            Marks = {
                sinc,
                damped,
                pole,
            },
            // 1/x explodes near zero — pin the y window; the pole itself breaks the stroke.
            YScale = new LinearScale {
                Min = -1.5,
                Max = 1.5,
                Nice = false,
            },
            ScrollableX = true,
            ZoomableX = true,
            VisibleXDomainLength = 12,
            XAxis = { ShowGrid = true },
        };

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children: [
                Box(chart, 260f),
                new SizedBox(height: 8),
                new Text(
                    "Sampled per pixel from the visible window — pan/zoom re-samples, the 1/x pole splits the curve",
                    new TextStyle(12, color: Colors.Grey[500])
                ),
            ]
        );
    }

    // ── Custom axis: pinned tick values + per-tick emphasis styling ───────────────
    public static Widget CustomAxis()
    {
        var rng = new Random(29);
        var data = new List<Pt>();
        double ms = 140;
        for (var i = 0; i < 48; i++)
        {
            ms = Math.Clamp(ms + (rng.NextDouble() - 0.47) * 60, 30, 390);
            data.Add(new Pt(i, ms));
        }

        var line = LineMark.Of(data, d => d.X, d => d.Y);
        line.Name = "p95 latency";
        line.Interpolation = ChartInterpolation.Monotone;
        line.Color = Color.Rgb(10, 132, 255);

        var slo = Color.Rgb(255, 59, 48);
        var chart = new Chart {
            Marks = { line },
            YScale = new LinearScale {
                Min = 0,
                Max = 400,
                Nice = false,
            },
            YAxis = {
                TickValues = [0.0, 100.0, 200.0, 300.0, 400.0],
                Formatter = v => $"{v.Numeric:F0} ms",
                // The 300 ms SLO tick gets an emphasized red grid line + label.
                TickStyle = v => v.Numeric == 300
                    ? new AxisTickStyle {
                        GridColor = slo.WithAlpha(0.6f),
                        GridWidth = 2f,
                        LabelColor = slo,
                    }
                    : default,
            },
            XAxis = { Title = "deploy" },
        };

        return Box(chart, 220f);
    }

    // ── Custom overlay through ChartProxy: target band + live marker ─────────────
    public static Widget ProxyOverlay()
    {
        var rng = new Random(41);
        var data = new List<Pt>();
        double v = 52;
        for (var i = 0; i < 40; i++)
        {
            v = Math.Clamp(v + (rng.NextDouble() - 0.5) * 14, 15, 90);
            data.Add(new Pt(i, v));
        }

        var line = LineMark.Of(data, d => d.X, d => d.Y);
        line.Interpolation = ChartInterpolation.Monotone;
        line.Color = Color.Rgb(88, 86, 214);

        var green = Color.Rgb(52, 199, 89);
        var last = data[^1];
        var chart = new Chart {
            Marks = { line },
            YScale = new LinearScale {
                Min = 0,
                Max = 100,
                Nice = false,
            },
            YAxis = { Title = "score" },
        };
        // Everything below projects through the proxy each paint, so the band and the marker track
        // scroll/zoom/morph for free. Hot path — geometry only, no strings.
        chart.OverlayPainter = (paint, proxy) =>
        {
            var plot = proxy.PlotRect;
            var top = proxy.PositionY(65);
            var bottom = proxy.PositionY(45);
            paint.AddRect(
                new Rect(
                    plot.X,
                    top,
                    plot.Width,
                    bottom - top
                ),
                green.WithAlpha(0.12f)
            );
            paint.AddRect(
                new Rect(
                    plot.X,
                    proxy.PositionY(55),
                    plot.Width,
                    1f
                ),
                green.WithAlpha(0.7f)
            );

            // Pulse ring on the newest datum.
            var p = proxy.Position(last.X, last.Y);
            paint.AddRect(
                new Rect(
                    p.X - 6f,
                    p.Y - 6f,
                    12f,
                    12f
                ),
                green.WithAlpha(0.35f),
                6f
            );
        };

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children: [
                Box(chart, 220f),
                new SizedBox(height: 8),
                new Text(
                    "Target band, mean line, and marker drawn by OverlayPainter via ChartProxy",
                    new TextStyle(12, color: Colors.Grey[500])
                ),
            ]
        );
    }

    // ── Interactive donut: tap a slice to read it out ────────────────────────────
    public static Widget InteractiveDonut()
    {
        var data = new List<Slice> {
            new("Desktop", 42),
            new("Mobile", 31),
            new("Console", 17),
            new("Web", 10),
        };

        var donut = SectorMark.Of(data, s => s.Share, s => s.Name);
        donut.InnerRadiusFraction = 0.6f;

        var readout = new Text(
            "Tap a slice",
            new TextStyle(12, color: Colors.Grey[500])
        );

        var chart = new Chart {
            Marks = { donut },
            OnPointTap = info =>
            {
                if (info.Points.Count == 0) return;
                var p = info.Points[0];
                readout.Text = $"{p.Series}: {p.ValueLabel}";
            },
        };

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children: [
                Box(chart, 220f),
                new SizedBox(height: 8),
                readout,
            ]
        );
    }

    private readonly record struct Pt(double X, double Y);

    private readonly record struct Share(string Month, string Platform, double V);

    private readonly record struct Flow(double T, string Genre, double V);

    private readonly record struct Bar(string M, double Price, double Vol);

    private readonly record struct Bar2(string M, double V);

    private readonly record struct Cell(string Day, string Hour, double V);

    private sealed record Slice(string Name, double Share);
}
