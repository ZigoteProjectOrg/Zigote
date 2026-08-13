using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Charts.Scales;
using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

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
        for (double seed = -WindowSeconds; seed < 0; seed += SampleInterval)
        {
            _t = seed;
            Emit();
        }

        _t = 0;

        var cpu = LineMark.Of(data: _cpu, x: s => s.T, y: s => s.V);
        cpu.Name = "CPU";
        cpu.Interpolation = ChartInterpolation.Monotone;
        cpu.Color = Color.Rgb(r: 10, g: 132, b: 255);

        var gpu = LineMark.Of(data: _gpu, x: s => s.T, y: s => s.V);
        gpu.Name = "GPU";
        gpu.Interpolation = ChartInterpolation.Monotone;
        gpu.Color = Color.Rgb(r: 255, g: 149, b: 0);

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
            value: _cpuLevel + ((_rng.NextDouble() - 0.5) * 9) + (Math.Sin(_t * 0.7) * 1.4),
            min: 4,
            max: 96
        );
        _gpuLevel = Math.Clamp(
            value: _gpuLevel + ((_rng.NextDouble() - 0.5) * 7) + (Math.Sin((_t * 0.4) + 1) * 1.1),
            min: 4,
            max: 96
        );
        _cpu.Add(new Sample(T: _t, V: _cpuLevel));
        _gpu.Add(new Sample(T: _t, V: _gpuLevel));
    }

    private void OnTick(float dt)
    {
        _t += dt;
        _sinceSample += dt;

        bool changed = false;
        while (_sinceSample >= SampleInterval)
        {
            _sinceSample -= SampleInterval;
            Emit();
            changed = true;
        }

        if (!changed) return;

        // Bound memory: drop samples that have scrolled off the left edge (+ a little slack).
        double cutoff = _t - WindowSeconds - 1.0;
        _cpu.RemoveAll(s => s.T < cutoff);
        _gpu.RemoveAll(s => s.T < cutoff);

        _chart.InvalidateData();
        _chart.ScrollToEnd();
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner: owner, parent: parent);
        _chart.Attach(owner: owner, parent: this);
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
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _chart.Layout(origin);
    }

    public override void Paint(PaintList paint) => _chart.Paint(paint);

    public override Widget? HitTest(Offset point) => _chart.HitTest(point);

    public override IEnumerable<Widget> GetChildren() => [_chart];

    private readonly record struct Sample(double T, double V);
}

/// <summary>Builders for the advanced/interactive chart demos shown in the gallery's Charts tab.</summary>
internal static class DemoCharts
{
    private static Widget Box(Chart chart, float height = 240f) =>
        new SizedBox(height: height, child: chart);

    // ── Scroll + zoom over a dense series ────────────────────────────────────────
    public static Widget ZoomPan()
    {
        var rng = new Random(11);
        var data = new List<Pt>();
        double price = 120;
        for (int i = 0; i < 220; i++)
        {
            price = Math.Max(val1: 20, val2: price + ((rng.NextDouble() - 0.48) * 9));
            data.Add(new Pt(X: i, Y: price));
        }

        var line = LineMark.Of(data: data, x: d => d.X, y: d => d.Y);
        line.Interpolation = ChartInterpolation.Monotone;
        line.Color = Color.Rgb(r: 52, g: 199, b: 89);

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
                Box(chart: chart, height: 250f),
                new SizedBox(height: 8),
                new Row(
                    [
                        new Text(
                            data: "Drag to pan · ⌘/Ctrl-scroll to zoom",
                            style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
                        ),
                        new Spacer(),
                        new OutlinedButton(
                            child: new Text("Reset zoom"),
                            onPressed: () =>
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
        for (int i = 0; i < 60; i++)
        {
            v = Math.Clamp(value: v + ((rng.NextDouble() - 0.5) * 16), min: 8, max: 92);
            data.Add(new Pt(X: i, Y: v));
        }

        var area = AreaMark.Of(data: data, x: d => d.X, y: d => d.Y);
        area.Interpolation = ChartInterpolation.Monotone;
        area.Color = Color.Rgb(r: 88, g: 86, b: 214);

        var readout = new Text(
            data: "Drag across the plot to select a range",
            style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
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

                int lo = (int)Math.Round(r.Min);
                int hi = (int)Math.Round(r.Max);
                var slice = data.Where(d => d.X >= lo && d.X <= hi).ToList();
                double mean = slice.Count > 0 ? slice.Average(d => d.Y) : 0;
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
        for (int i = 0; i < months.Length; i++)
        {
            price += (rng.NextDouble() - 0.4) * 40;
            rows.Add(new Bar(M: months[i], Price: price, Vol: 20 + (rng.NextDouble() * 80)));
        }

        var volume = BarMark.Of(data: rows, x: d => d.M, y: d => d.Vol);
        volume.Name = "Volume";
        volume.UseSecondaryYAxis = true;
        volume.CornerRadius = 3f;
        volume.Color = Color.Rgba(
            r: 120,
            g: 128,
            b: 140,
            a: 0.55f
        );

        var priceLine = LineMark.Of(data: rows, x: d => d.M, y: d => d.Price);
        priceLine.Name = "Price";
        priceLine.Interpolation = ChartInterpolation.Monotone;
        priceLine.ShowSymbols = true;
        priceLine.Color = Color.Rgb(r: 255, g: 59, b: 48);

        var chart = new Chart {
            Marks = {
                volume,
                priceLine,
            }, // bars first so the line paints on top
            YAxis = { Title = "price $" },
            YAxis2 = { Title = "volume" },
        };

        return Box(chart: chart, height: 250f);
    }

    // ── Heatmap: activity by weekday × hour band, colour = magnitude ─────────────
    public static Widget Heatmap()
    {
        string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        string[] hours = ["0", "3", "6", "9", "12", "15", "18", "21"];
        var rng = new Random(5);
        var cells = new List<Cell>();
        for (int d = 0; d < days.Length; d++)
        for (int h = 0; h < hours.Length; h++)
        {
            // Weekday work hours peak; weekends flatter.
            double work = (h is >= 3 and <= 5 ? 1.0 : 0.35) * (d < 5 ? 1.0 : 0.5);
            cells.Add(
                new Cell(Day: days[d], Hour: hours[h], V: work * (0.5 + rng.NextDouble()) * 100)
            );
        }

        var heat = RectangleMark.Of(data: cells, x: c => c.Day, y: c => c.Hour);
        heat.FillBy = c => c.V;
        heat.LowColor = Color.Rgba(
            r: 10,
            g: 132,
            b: 255,
            a: 0.10f
        );
        heat.HighColor = Color.Rgb(r: 10, g: 132, b: 255);
        heat.CornerRadius = 3f;

        var chart = new Chart {
            Marks = { heat },
            LegendPosition = ChartLegendPosition.Hidden,
            XAxis = { Title = "weekday" },
            YAxis = { Title = "hour" },
        };

        return Box(chart: chart, height: 220f);
    }

    // ── Composed marks: bars + a target threshold rule + a trend line + annotation ─
    public static Widget ThresholdTrend()
    {
        string[] q = ["W1", "W2", "W3", "W4", "W5", "W6", "W7", "W8"];
        double[] vals = [42, 58, 51, 73, 66, 91, 78, 84];
        var rows = new List<Bar2>();
        for (int i = 0; i < q.Length; i++) rows.Add(new Bar2(M: q[i], V: vals[i]));

        // Peak marker.
        int peakIdx = 0;
        for (int i = 1; i < vals.Length; i++)
        {
            if (vals[i] > vals[peakIdx])
                peakIdx = i;
        }

        var bars = BarMark.Of(data: rows, x: d => d.M, y: d => d.V);
        bars.Name = "Weekly";
        bars.Color = Color.Rgba(
            r: 10,
            g: 132,
            b: 255,
            a: 0.85f
        );

        var trend = LineMark.Of(data: rows, x: d => d.M, y: d => d.V);
        trend.Name = "Trend";
        trend.Interpolation = ChartInterpolation.Monotone;
        trend.Color = Color.Rgb(r: 255, g: 149, b: 0);

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

        return Box(chart: chart, height: 220f);
    }

    // ── Normalized (100%) stacked bars: each column rescaled to its total ────────
    public static Widget NormalizedStack()
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun"];
        string[] platforms = ["Desktop", "Mobile", "Web"];
        var rng = new Random(17);
        var rows = new List<Share>();
        foreach (string m in months)
        foreach (string p in platforms)
            rows.Add(new Share(Month: m, Platform: p, V: 20 + (rng.NextDouble() * 80)));

        var bars = BarMark.Of(
            data: rows,
            x: d => d.Month,
            y: d => d.V,
            series: d => d.Platform
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

        return Box(chart: chart, height: 220f);
    }

    // ── Streamgraph: center-stacked areas, silhouette around zero ────────────────
    public static Widget Streamgraph()
    {
        string[] genres = ["Action", "Puzzle", "Racing", "Sim"];
        var rows = new List<Flow>();
        for (int i = 0; i <= 30; i++)
        for (int g = 0; g < genres.Length; g++)
        {
            double v = 12 + (10 * Math.Sin((i / 4.5) + (g * 1.7))) + (6 * Math.Sin((i / 2.1) + g)) +
                       (g * 3);
            rows.Add(new Flow(T: i, Genre: genres[g], V: Math.Max(val1: 1, val2: v)));
        }

        var stream = AreaMark.Of(
            data: rows,
            x: d => d.T,
            y: d => d.V,
            series: d => d.Genre
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

        return Box(chart: chart, height: 220f);
    }

    // ── Function plots: y = f(x) sampled per pixel, poles break the stroke ───────
    public static Widget FunctionPlot()
    {
        var sinc =
            new FunctionLineMark(
                function: x => x == 0 ? 1.0 : Math.Sin(x * 2) / (x * 2),
                xMin: -12,
                xMax: 12
            ) {
                Name = "sinc 2x",
                Color = Color.Rgb(r: 10, g: 132, b: 255),
            };
        var damped = new FunctionLineMark(
            function: x => Math.Cos(3 * x) * Math.Exp(-Math.Abs(x) / 6.0),
            xMin: -12,
            xMax: 12
        ) {
            Name = "cos 3x · e^(-|x|/6)",
            Color = Color.Rgb(r: 255, g: 149, b: 0),
        };
        var pole = new FunctionLineMark(function: x => 1.0 / x, xMin: -12, xMax: 12) {
            Name = "1/x",
            Color = Color.Rgb(r: 255, g: 59, b: 48),
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
                Box(chart: chart, height: 260f),
                new SizedBox(height: 8),
                new Text(
                    data:
                    "Sampled per pixel from the visible window — pan/zoom re-samples, the 1/x pole splits the curve",
                    style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
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
        for (int i = 0; i < 48; i++)
        {
            ms = Math.Clamp(value: ms + ((rng.NextDouble() - 0.47) * 60), min: 30, max: 390);
            data.Add(new Pt(X: i, Y: ms));
        }

        var line = LineMark.Of(data: data, x: d => d.X, y: d => d.Y);
        line.Name = "p95 latency";
        line.Interpolation = ChartInterpolation.Monotone;
        line.Color = Color.Rgb(r: 10, g: 132, b: 255);

        var slo = Color.Rgb(r: 255, g: 59, b: 48);
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

        return Box(chart: chart, height: 220f);
    }

    // ── Custom overlay through ChartProxy: target band + live marker ─────────────
    public static Widget ProxyOverlay()
    {
        var rng = new Random(41);
        var data = new List<Pt>();
        double v = 52;
        for (int i = 0; i < 40; i++)
        {
            v = Math.Clamp(value: v + ((rng.NextDouble() - 0.5) * 14), min: 15, max: 90);
            data.Add(new Pt(X: i, Y: v));
        }

        var line = LineMark.Of(data: data, x: d => d.X, y: d => d.Y);
        line.Interpolation = ChartInterpolation.Monotone;
        line.Color = Color.Rgb(r: 88, g: 86, b: 214);

        var green = Color.Rgb(r: 52, g: 199, b: 89);
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
            float top = proxy.PositionY(65);
            float bottom = proxy.PositionY(45);
            paint.AddRect(
                bounds: new Rect(
                    x: plot.X,
                    y: top,
                    width: plot.Width,
                    height: bottom - top
                ),
                color: green.WithAlpha(0.12f)
            );
            paint.AddRect(
                bounds: new Rect(
                    x: plot.X,
                    y: proxy.PositionY(55),
                    width: plot.Width,
                    height: 1f
                ),
                color: green.WithAlpha(0.7f)
            );

            // Pulse ring on the newest datum.
            var p = proxy.Position(x: last.X, y: last.Y);
            paint.AddRect(
                bounds: new Rect(
                    x: p.X - 6f,
                    y: p.Y - 6f,
                    width: 12f,
                    height: 12f
                ),
                color: green.WithAlpha(0.35f),
                radius: 6f
            );
        };

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children: [
                Box(chart: chart, height: 220f),
                new SizedBox(height: 8),
                new Text(
                    data:
                    "Target band, mean line, and marker drawn by OverlayPainter via ChartProxy",
                    style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
                ),
            ]
        );
    }

    // ── Interactive donut: tap a slice to read it out ────────────────────────────
    public static Widget InteractiveDonut()
    {
        var data = new List<Slice> {
            new(Name: "Desktop", Share: 42),
            new(Name: "Mobile", Share: 31),
            new(Name: "Console", Share: 17),
            new(Name: "Web", Share: 10),
        };

        var donut = SectorMark.Of(data: data, value: s => s.Share, category: s => s.Name);
        donut.InnerRadiusFraction = 0.6f;

        var readout = new Text(
            data: "Tap a slice",
            style: new TextStyle(fontSize: 12, color: Colors.Grey[500])
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
                Box(chart: chart, height: 220f),
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
