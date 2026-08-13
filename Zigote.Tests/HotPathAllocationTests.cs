using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.LiquidGlass;

namespace Zigote.Tests;

/// <summary>
///     Regression guard for the zero-GC hot path: a steady-state Measure → Layout → Paint pass over a
///     representative retained tree must allocate nothing on the managed heap. Widget instances
///     persist
///     across frames (their fields are their state), the <see cref="PaintList" /> reuses its buffers,
///     and text sizing / UTF-8 encoding are memoised — so after warm-up a repeated frame should cost
///     zero bytes. Measured with <see cref="GC.GetAllocatedBytesForCurrentThread" />, which is exact
///     and
///     deterministic for a single-threaded loop.
/// </summary>
public class HotPathAllocationTests
{
    // A nested tree exercising the layout kernel (Column/Row/Padding/Center/SizedBox/ColoredBox) plus
    // text leaves (Label) — the widgets a real frame paints constantly.
    private static Widget BuildTree()
    {
        return new ColoredBox(
            Color.White,
            new Padding(
                EdgeInsets.All(8f),
                new Column(
                    [
                        new Row(
                            [
                                new SizedBox(24f, 24f),
                                new SizedBox(8f, 0f),
                                new Label("Toolbar"),
                            ]
                        ),
                        new SizedBox(0f, 8f),
                        new Center(new Label("Body content goes here")),
                        new SizedBox(0f, 8f),
                        new Row(
                            [
                                new Label("Alpha"),
                                new SizedBox(8f, 0f),
                                new Label("Beta"),
                                new SizedBox(8f, 0f),
                                new Label("Gamma"),
                            ]
                        ),
                    ]
                )
            )
        );
    }

    private static void Frame(Widget root, PaintList paint, Constraints c)
    {
        paint.Clear();
        root.Measure(c);
        root.Layout(Offset.Zero);
        root.Paint(paint);
    }

    [Fact]
    public void MeasureLayoutPaint_AllocatesZero_OnSteadyState()
    {
        var root = BuildTree();
        var paint = new PaintList();
        var c = Constraints.Tight(800f, 600f);

        // Warm up past tiered JIT and populate the Utf8 / TextMeasure / PaintList-capacity caches.
        for (var i = 0; i < 200; i++) Frame(root, paint, c);

        // Sanity: the tree actually produced paint commands (the loop isn't a no-op).
        Assert.True(paint.Count > 0);

        const int frames = 500;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++) Frame(root, paint, c);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"Hot path allocated {allocated} B over {frames} frames " +
            $"({allocated / (double)frames:F2} B/frame); expected 0."
        );
    }

    // GlassGlow used to re-run its recursive FindLiquidGlass tree walk (boxed GetChildren
    // enumerators) on every Paint; the resolved LiquidGlass is now cached per child instance.
    [Fact]
    public void GlassGlow_SteadyStatePaint_AllocatesZero()
    {
        var root = new GlassGlow(
            new Padding(
                EdgeInsets.All(8f),
                new LiquidGlass(new Label("Glass"))
            )
        );
        var paint = new PaintList();
        var c = Constraints.Tight(400f, 300f);

        AllocGuard.AssertZeroAlloc(() => Frame(root, paint, c));
        Assert.True(paint.Count > 0);
    }
}
