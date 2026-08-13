using System.Diagnostics;
using System.Globalization;
using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
// Slider / Switch / TextField / Divider / ProgressBar / Badge come from Zigote.UI.Material,
// which the test project pulls in via a global <Using> in Zigote.Tests.csproj. Alias Switch to
// disambiguate it from System.Diagnostics.Switch (pulled in for Stopwatch).
using Switch = Zigote.UI.Material.Switch;

namespace Zigote.Tests;

/// <summary>
///     Performance benchmark over a <em>realistic</em> interactive widget tree — a scrollable settings
///     page built from the actual control set (Buttons, Checkboxes, Switches, Sliders, TextField,
///     Cards, Dividers, ProgressBar, Chips, Badge) wrapped in a <see cref="ThemeProvider" />, plus a
///     virtualized <see cref="ListView" />. This is the counterpart to the uniform-grid
///     <c>UiPerformanceTests</c>: it drives the same headless Measure → Layout → Paint pipeline that a
///     real frame runs (stateful controls build through <see cref="BuildContext" />, controls resolve
///     the theme), and reports per-phase cost, paint throughput, and steady-state allocation through
///     the
///     xUnit output. Run with:
///     <c>
///         dotnet test Zigote.Tests/Zigote.Tests.csproj -c Release --filter ComplexUiPerformanceTests
///         -l "console;verbosity=detailed"
///     </c>
/// </summary>
public class ComplexUiPerformanceTests
{
    private const double FrameBudgetMs = 16.67; // 60 Hz

    private readonly ITestOutputHelper _output;

    public ComplexUiPerformanceTests(ITestOutputHelper output) => _output = output;

    // One settings "group": a header, a divider, and several labelled control rows. Mirrors what a
    // real form section looks like — heterogeneous controls, nesting, flex rows with a Spacer.
    private static Widget BuildPanel(int index)
    {
        return new Card(
            new Padding(
                padding: EdgeInsets.All(12f),
                child: new Column(
                    [
                        new Row(
                            [
                                new Label($"Section {index}"),
                                new Spacer(),
                                new Badge(child: new Label("beta"), count: index + 1),
                            ]
                        ),
                        new Divider(),
                        LabeledRow(label: "Dark mode", control: new Switch(index % 2 == 0)),
                        LabeledRow(label: "Notifications", control: new Checkbox(index % 3 == 0)),
                        LabeledRow(
                            label: "Volume",
                            control: new SizedBox(
                                width: 160f,
                                height: null,
                                child: new Slider(value: 0.5f + (index * 0.05f), min: 0, max: 1)
                            )
                        ),
                        new SizedBox(width: 0f, height: 8f),
                        new Label("Display name"),
                        new SizedBox(width: 0f, height: 4f),
                        new TextField(),
                        new SizedBox(width: 0f, height: 8f),
                        new ProgressBar(0.35f + (index * 0.05f)),
                        new SizedBox(width: 0f, height: 8f),
                        new Row(
                            [
                                new Chip(label: "All", selected: index == 0),
                                new SizedBox(width: 6f, height: 0f),
                                new Chip(label: "Unread", selected: index == 1),
                                new Spacer(),
                                new Button(label: "Reset", onPressed: null) {
                                    Style = ButtonStyle.Flat,
                                },
                                new SizedBox(width: 6f, height: 0f),
                                new Button(label: "Apply", onPressed: null),
                            ]
                        ),
                    ]
                )
            )
        );
    }

    private static Widget LabeledRow(string label, Widget control)
    {
        return new Padding(
            padding: EdgeInsets.Symmetric(horizontal: 0f, vertical: 4f),
            child: new Row(
                [
                    new SizedBox(width: 120f, height: null, child: new Label(label)), new Spacer(),
                    control,
                ]
            )
        );
    }

    // The whole page: N stacked panels inside a scroll view, followed by a virtualized list. The
    // ListView is windowed, so its paint cost reflects the visible rows only — exactly the point of
    // including it.
    private static Widget BuildPage(int panels, int listItems)
    {
        var column = new Column();
        column.Children.Add(new SizedBox(width: 0f, height: 8f, child: new Label("Preferences")));
        for (int i = 0; i < panels; i++)
        {
            column.Children.Add(BuildPanel(i));
            column.Children.Add(new SizedBox(width: 0f, height: 12f));
        }

        var items = new List<Widget>(listItems);
        for (int i = 0; i < listItems; i++)
        {
            items.Add(
                new Padding(
                    padding: EdgeInsets.All(6f),
                    child: new Row(
                        [new Label($"Row {i}"), new Spacer(), new Label($"#{i * 7 % 100}")]
                    )
                )
            );
        }

        column.Children.Add(
            new SizedBox(
                width: 0f,
                height: 260f,
                child: new ListView(children: items, itemExtent: 34)
            )
        );

        return new ThemeProvider(
            data: ThemeData.Dark,
            child: new ScrollView(new Padding(padding: EdgeInsets.All(16f), child: column))
        );
    }

    private static void Frame(Widget root, PaintList paint, Constraints c)
    {
        paint.Clear();
        root.Measure(c);
        root.Layout(Offset.Zero);
        root.Paint(paint);
    }

    private static double TimeMsPerIter(int iterations, Action body)
    {
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) body();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds / iterations;
    }

    private static string F(double v) => v.ToString(
        format: "F4",
        provider: CultureInfo.InvariantCulture
    );

    [Fact]
    public void RealisticSettingsPage_MeasureLayoutPaint_CollectsMetrics()
    {
        var viewport = Constraints.Tight(width: 480f, height: 900f); // phone-ish portrait window
        (int panels, int items)[] sizes = [
            (1, 50),
            (3, 200),
            (6, 500),
        ];

        _output.WriteLine("Realistic settings page — Measure → Layout → Paint (480×900 viewport)");
        _output.WriteLine(
            "  panels | listItems | paintCmds | measure ms |  layout ms |   paint ms |  frame ms | ns/cmd"
        );
        _output.WriteLine(
            "  -------+-----------+-----------+------------+------------+------------+-----------+-------"
        );

        foreach ((int panels, int items) in sizes)
        {
            var root = BuildPage(panels: panels, listItems: items);
            var paint = new PaintList();

            for (int i = 0; i < 100; i++) Frame(root: root, paint: paint, c: viewport);
            int paintCmds = paint.Count;
            Assert.True(
                condition: paintCmds > 0,
                userMessage: "the page must produce paint commands"
            );

            double frameMs = TimeMsPerIter(
                iterations: 300,
                body: () => Frame(root: root, paint: paint, c: viewport)
            );

            double measureMs = TimeMsPerIter(iterations: 300, body: () => root.Measure(viewport));
            root.Measure(viewport);
            double layoutMs = TimeMsPerIter(iterations: 300, body: () => root.Layout(Offset.Zero));
            root.Layout(Offset.Zero);
            double paintMs = TimeMsPerIter(
                iterations: 300,
                body: () =>
                {
                    paint.Clear();
                    root.Paint(paint);
                }
            );

            double nsPerCmd = paintMs * 1_000_000.0 / paintCmds;

            _output.WriteLine(
                $"  {panels,6} | {items,9} | {paintCmds,9} | {F(measureMs),10} | {F(layoutMs),10} | " +
                $"{F(paintMs),10} | {F(frameMs),9} | {nsPerCmd,6:F0}"
            );

            Assert.True(
                condition: frameMs < FrameBudgetMs,
                userMessage:
                $"{panels}-panel page frame cost {F(frameMs)} ms exceeded the {FrameBudgetMs} ms budget"
            );
        }
    }

    [Fact]
    public void RealisticSettingsPage_SteadyStateAllocation_CollectsMetrics()
    {
        var viewport = Constraints.Tight(width: 480f, height: 900f);
        var root = BuildPage(panels: 4, listItems: 300);
        var paint = new PaintList();

        // Warm past tiered JIT and populate the text-measure / paint-buffer / build caches.
        for (int i = 0; i < 200; i++) Frame(root: root, paint: paint, c: viewport);

        const int frames = 300;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) Frame(root: root, paint: paint, c: viewport);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double perFrame = allocated / (double)frames;

        _output.WriteLine("Realistic settings page — steady-state managed allocation");
        _output.WriteLine($"  frames   : {frames}");
        _output.WriteLine($"  total B  : {allocated}");
        _output.WriteLine($"  B/frame  : {perFrame:F2}");

        // A realistic tree mixes hot-path layout widgets with controls that may still allocate a little
        // per frame (ticker/animation state, virtualized-list windowing). This guards against a gross
        // steady-state leak while tolerating small incidental churn — the exact number is logged above.
        Assert.True(
            condition: perFrame < 2048,
            userMessage:
            $"steady-state allocation was {perFrame:F2} B/frame over {frames} frames; " +
            "expected < 2048 B/frame (a realistic page should be near-zero-GC)."
        );
    }

    [Fact]
    public void RealisticSettingsPage_ColdBuildCost_CollectsMetrics()
    {
        // First-frame cost: constructing the retained tree + the very first Measure/Layout/Paint,
        // which fires every widget's OnMount/Build and warms text measurement.
        // This is the "time to first frame" a screen pays once — distinct from the steady-state cost.
        var viewport = Constraints.Tight(width: 480f, height: 900f);

        var buildSw = Stopwatch.StartNew();
        var root = BuildPage(panels: 4, listItems: 300);
        buildSw.Stop();

        var paint = new PaintList();
        var firstSw = Stopwatch.StartNew();
        Frame(root: root, paint: paint, c: viewport);
        firstSw.Stop();

        _output.WriteLine("Realistic settings page — cold cost (4 panels, 300 list items)");
        _output.WriteLine($"  construct tree      : {F(buildSw.Elapsed.TotalMilliseconds)} ms");
        _output.WriteLine($"  first frame (M+L+P) : {F(firstSw.Elapsed.TotalMilliseconds)} ms");
        _output.WriteLine($"  paint commands      : {paint.Count}");

        Assert.True(paint.Count > 0);
    }
}
