using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     The Zigote.UI.Charts showcase. Built once per visit (the route caches the content), so chart
///     animation/interaction state survives while the page is on the stack.
/// </summary>
internal sealed class ChartsPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        // The window's size class, not the pane's: the grid sections below sit in cells that are
        // only ~540 px wide on a desktop window, so an AdaptiveBuilder inside one would read
        // Compact there and hand the desktop the phone arm.
        var size = MediaQuery.Of(context).SizeClass;

        var sales = new List<(string Month, double Rev, string Region)> {
            ("Jan", 320, "West"),
            ("Feb", 410, "West"),
            ("Mar", 380, "West"),
            ("Apr", 470, "West"),
            ("May", 540, "West"),
            ("Jan", 210, "East"),
            ("Feb", 240, "East"),
            ("Mar", 305, "East"),
            ("Apr", 280, "East"),
            ("May", 335, "East"),
        };

        var series = new List<(double X, double Y, string S)>();
        for (int i = 0; i < 24; i++)
        {
            series.Add((i, 20 + (8 * Math.Sin(i / 3.0)), "A"));
            series.Add((i, 26 + (6 * Math.Sin((i / 2.4) + 1)), "B"));
        }

        var bubbles = new List<(double X, double Y, double R, string S)> {
            (2, 6.5, 30, "Core"),
            (4.5, 8, 80, "Core"),
            (6, 4, 45, "Core"),
            (3, 3, 20, "Growth"),
            (5.5, 6.8, 95, "Growth"),
            (7.5, 7.5, 60, "Growth"),
        };

        var platforms = new List<(string Name, double Share)> {
            ("Desktop", 42),
            ("Mobile", 31),
            ("Console", 17),
            ("Web", 10),
        };

        var bench = new List<(string Engine, double Fps)> {
            ("Zigote", 244),
            ("Engine A", 187),
            ("Engine B", 156),
            ("Engine C", 121),
        };

        var quarters = new List<Quarter> {
            new(name: "Q1", value: 62),
            new(name: "Q2", value: 78),
            new(name: "Q3", value: 41),
            new(name: "Q4", value: 95),
        };
        var animated = new Chart {
            Marks = { BarMark.Of(data: quarters, x: q => q.Name, y: q => q.Value) },
            YAxis = { Title = "score" },
        };
        var rand = new Random();

        var bubbleMark = PointMark.Of(
            data: bubbles,
            x: d => d.X,
            y: d => d.Y,
            series: d => d.S
        );
        bubbleMark.SizeBy = d => d.R;

        var donutMark = SectorMark.Of(data: platforms, value: d => d.Share, category: d => d.Name);
        donutMark.InnerRadiusFraction = 0.6f;

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children: [
                // ── Advanced / interactive / animated (full width — they need the room) ──
                Section(title: "Live metrics — real-time stream", child: new LiveLineChart()),
                // Zoom is bound to modifier + wheel; touch has neither, so a phone must not be told
                // to use a gesture it cannot make (pan works — a horizontal drag survives the
                // touch arena).
                Section(
                    title: size == WindowSizeClass.Compact
                        ? "Scroll & zoom — drag to pan"
                        : "Scroll & zoom — drag to pan, ⌘/Ctrl-scroll to zoom",
                    child: DemoCharts.ZoomPan()
                ),
                Section(
                    title: "Range selection — drag across the plot",
                    child: DemoCharts.RangeSelection()
                ),
                Section(
                    title: "Dual axis — price line + volume bars",
                    child: DemoCharts.DualAxis()
                ),
                Section(
                    title: "Function plot — y = f(x), pan/zoom re-samples",
                    child: DemoCharts.FunctionPlot()
                ),

                // ── Compact gallery of mark types (two-column grid) ──
                Grid2(
                    Section(
                        title: "Grouped bars — revenue by month × region",
                        child: ChartBox(
                            new Chart {
                                Marks = {
                                    BarMark.Of(
                                        data: sales,
                                        x: d => d.Month,
                                        y: d => d.Rev,
                                        series: d => d.Region
                                    ),
                                },
                                YAxis = { Title = "K$" },
                            }
                        )
                    ),
                    Section(
                        title: "Multi-series line",
                        child: ChartBox(
                            new Chart {
                                Marks = {
                                    LineMark.Of(
                                        data: series,
                                        x: d => d.X,
                                        y: d => d.Y,
                                        series: d => d.S
                                    ),
                                },
                                YAxis = { Title = "°C" },
                            }
                        )
                    ),
                    Section(
                        title: "Stacked area",
                        child: ChartBox(
                            new Chart {
                                Marks = {
                                    AreaMark.Of(
                                        data: sales,
                                        x: d => d.Month,
                                        y: d => d.Rev,
                                        series: d => d.Region
                                    ),
                                },
                            }
                        )
                    ),
                    Section(
                        title: "Bubble scatter (size = reach)",
                        child: ChartBox(
                            new Chart {
                                Marks = { bubbleMark },
                                XAxis = {
                                    Title = "Effort",
                                    ShowGrid = true,
                                },
                                YAxis = { Title = "Impact" },
                            }
                        )
                    ),
                    Section(title: "Donut", child: ChartBox(new Chart { Marks = { donutMark } })),
                    Section(
                        title: "Horizontal bars",
                        child: ChartBox(
                            new Chart {
                                Marks = {
                                    BarMark.Of(data: bench, x: d => d.Fps, y: d => d.Engine),
                                },
                                XAxis = { Title = "fps" },
                            }
                        )
                    ),
                    Section(
                        title: "Animated data updates",
                        child: new Column(
                            crossAxisAlignment: CrossAxisAlignment.Stretch,
                            children: [
                                ChartBox(animated),
                                // Desktop breathing room under the chart; a phone screen has none
                                // to spare once the grid collapses to one full-width column.
                                new SizedBox(height: size == WindowSizeClass.Compact ? 12 : 40),
                                new ElevatedButton(
                                    child: new Text("Shuffle data"),
                                    onPressed: () =>
                                    {
                                        foreach (var q in quarters)
                                            q.Value = 15 + (rand.NextDouble() * 90);
                                        animated.InvalidateData(true);
                                    }
                                ),
                            ]
                        )
                    ),
                    Section(
                        title: "Heatmap — activity by weekday × hour",
                        child: DemoCharts.Heatmap()
                    ),
                    Section(
                        title: "Threshold + trend + annotation",
                        child: DemoCharts.ThresholdTrend()
                    ),
                    Section(
                        title: "Interactive donut — tap a slice",
                        child: DemoCharts.InteractiveDonut()
                    ),
                    Section(
                        title: "100% stacked — normalized per column",
                        child: DemoCharts.NormalizedStack()
                    ),
                    Section(
                        title: "Streamgraph — center-stacked areas",
                        child: DemoCharts.Streamgraph()
                    ),
                    Section(
                        title: "Custom axis — pinned ticks, SLO emphasized",
                        child: DemoCharts.CustomAxis()
                    ),
                    Section(title: "Overlay painter — ChartProxy", child: DemoCharts.ProxyOverlay())
                ),
            ]
        );
    }

    private sealed class Quarter(string name, double value)
    {
        public string Name { get; } = name;
        public double Value { get; set; } = value;
    }
}
