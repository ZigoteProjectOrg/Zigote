using System.Diagnostics;
using System.Globalization;
using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Headless UI performance benchmarks. Each test builds a retained widget tree, runs the real
///     Measure → Layout → Paint pipeline over it (no native window), and reports metrics through the
///     xUnit test output: per-phase CPU cost, full-frame cost, paint-command throughput, and
///     steady-state managed allocation. The heavier <see cref="Fact" />s double as loose regression
///     guards (a 60 Hz frame budget) — the numbers themselves are logged so a run can be diffed
///     against a prior one. Run with:
///     <c>
///         dotnet test Zigote.Tests/Zigote.Tests.csproj --filter UiPerformanceTests -l
///         "console;verbosity=detailed"
///     </c>
/// </summary>
public class UiPerformanceTests
{
    // 60 Hz = 16.67 ms/frame. The pure-CPU Measure/Layout/Paint pass should sit far under a whole
    // frame even for a large tree; this is a generous ceiling that survives CI machine variance while
    // still catching an order-of-magnitude regression.
    private const double FrameBudgetMs = 16.67;

    private readonly ITestOutputHelper _output;

    public UiPerformanceTests(ITestOutputHelper output) => _output = output;

    // A rows×cols grid of text cells — a Column of Rows, each cell a centered Label inside a fixed
    // SizedBox. This exercises the flex layout kernel (Row/Column main+cross axis), a fixed-size box,
    // an alignment pass (Center), and a text leaf (Label) per cell: the mix a real dense UI paints.
    private static Widget BuildGrid(int rows, int cols)
    {
        var column = new Column();
        for (int r = 0; r < rows; r++)
        {
            var row = new Row();
            for (int c = 0; c < cols; c++)
            {
                row.Children.Add(
                    new SizedBox(width: 52f, height: 22f, child: new Center(new Label($"R{r}C{c}")))
                );
            }

            column.Children.Add(row);
        }

        return new ColoredBox(
            color: Color.White,
            child: new Padding(padding: EdgeInsets.All(8f), child: column)
        );
    }

    // Per-cell widget cost: SizedBox → Center → Label = 3. Plus ColoredBox + Padding + Column + one
    // Row per grid row. Kept as a formula so the reported "total widgets" needs no tree walk.
    private static int TotalWidgets(int rows, int cols) => 3 + rows + (rows * cols * 3);

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
    public void Scaling_MeasureLayoutPaint_CollectsMetrics()
    {
        var viewport = Constraints.Tight(width: 1920f, height: 1080f);
        (int rows, int cols)[] sizes = [
            (10, 6),
            (25, 10),
            (50, 16),
            (80, 20),
        ];

        _output.WriteLine("UI frame cost — Measure → Layout → Paint (1920×1080 viewport)");
        _output.WriteLine(
            "  leaves | widgets | paintCmds |  build ms | measure ms |  layout ms |   paint ms |  frame ms | ns/cmd"
        );
        _output.WriteLine(
            "  -------+---------+-----------+-----------+------------+------------+------------+-----------+-------"
        );

        foreach ((int rows, int cols) in sizes)
        {
            int leaves = rows * cols;

            double buildMs = TimeMsPerIter(
                iterations: 1,
                body: () => _ = BuildGrid(rows: rows, cols: cols)
            );

            var root = BuildGrid(rows: rows, cols: cols);
            var paint = new PaintList();

            // Warm past tiered JIT and populate the TextMeasure / Utf8 / PaintList-capacity caches.
            for (int i = 0; i < 100; i++) Frame(root: root, paint: paint, c: viewport);
            int paintCmds = paint.Count;
            Assert.True(
                condition: paintCmds > 0,
                userMessage: "the grid must produce paint commands"
            );

            // Full frame (all three phases).
            double frameMs = TimeMsPerIter(
                iterations: 300,
                body: () => Frame(root: root, paint: paint, c: viewport)
            );

            // Isolated phases. Measure feeds Layout feeds Paint, so re-run the prerequisites once and
            // then loop the phase under test — each phase reads state the previous one stored.
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
                $"  {leaves,6} | {TotalWidgets(rows: rows, cols: cols),7} | {paintCmds,9} | {F(buildMs),9} | " +
                $"{F(measureMs),10} | {F(layoutMs),10} | {F(paintMs),10} | {F(frameMs),9} | {nsPerCmd,6:F0}"
            );

            Assert.True(
                condition: frameMs < FrameBudgetMs,
                userMessage:
                $"{leaves}-cell grid frame cost {F(frameMs)} ms exceeded the {FrameBudgetMs} ms budget"
            );
        }
    }

    [Fact]
    public void SteadyState_ZeroAllocation_AcrossSizes()
    {
        var viewport = Constraints.Tight(width: 1440f, height: 900f);
        (int rows, int cols)[] sizes = [
            (10, 6),
            (40, 14),
        ];

        _output.WriteLine("Steady-state managed allocation (after warm-up)");
        _output.WriteLine("  leaves | frames | total B | B/frame");
        _output.WriteLine("  -------+--------+---------+--------");

        foreach ((int rows, int cols) in sizes)
        {
            var root = BuildGrid(rows: rows, cols: cols);
            var paint = new PaintList();

            for (int i = 0; i < 200; i++) Frame(root: root, paint: paint, c: viewport);

            const int frames = 500;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < frames; i++) Frame(root: root, paint: paint, c: viewport);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            _output.WriteLine(
                $"  {rows * cols,6} | {frames,6} | {allocated,7} | {allocated / (double)frames,7:F2}"
            );

            Assert.True(
                condition: allocated == 0,
                userMessage:
                $"{rows * cols}-cell grid allocated {allocated} B over {frames} frames " +
                $"({allocated / (double)frames:F2} B/frame); the hot path must be zero-GC."
            );
        }
    }

    [Fact]
    public void DeepVsWide_LayoutCost_CollectsMetrics()
    {
        var viewport = Constraints.Tight(width: 1280f, height: 800f);

        // Same leaf count (~600), two extreme shapes: one deeply nested column-of-rows vs one flat
        // wide row. Reveals whether cost tracks widget count or tree depth.
        var deep = BuildGrid(rows: 60, cols: 10); // 60 nested rows
        var wide = BuildGrid(rows: 6, cols: 100); //  6 rows, very wide

        var paint = new PaintList();
        foreach (var root in new[] {
                     deep,
                     wide,
                 })
        {
            for (int i = 0; i < 100; i++)
                Frame(root: root, paint: paint, c: viewport);
        }

        double deepMs = TimeMsPerIter(
            iterations: 300,
            body: () => Frame(root: deep, paint: paint, c: viewport)
        );
        double wideMs = TimeMsPerIter(
            iterations: 300,
            body: () => Frame(root: wide, paint: paint, c: viewport)
        );

        _output.WriteLine("Tree shape — frame cost at ~600 leaves");
        _output.WriteLine($"  deep (60×10 nested) : {F(deepMs)} ms/frame");
        _output.WriteLine($"  wide (6×100 flat)   : {F(wideMs)} ms/frame");

        Assert.True(deepMs < FrameBudgetMs && wideMs < FrameBudgetMs);
    }
}
