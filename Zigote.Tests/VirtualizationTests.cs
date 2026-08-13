using Xunit;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Guards windowed virtualization: <see cref="ListView" /> (uniform + variable height) and
///     <see cref="TreeView{T}" /> must only touch the rows inside the viewport, so a list/tree of
///     thousands of rows stays O(viewport), not O(count). The list probes whether off-screen rows were
///     ever measured/laid out; the tree counts paint commands emitted under a clip window.
/// </summary>
public class VirtualizationTests
{
    [Fact]
    public void UniformList_LaysOutOnlyVisibleWindow()
    {
        var probes = Enumerable.Range(0, 1000).Select(_ => new Probe(20f)).ToList();
        var lv = new ListView {
            ItemHeight = 20f,
            Smooth = false,
        };
        lv.SetItems(probes);

        lv.Measure(new Constraints(maxWidth: 300, maxHeight: 200)); // 200px / 20px ≈ 11 rows
        lv.Layout(Offset.Zero);

        var laidOut = probes.Count(p => p.Layouts > 0);
        Assert.InRange(laidOut, 1, 20); // a window, never all 1000
        Assert.True(probes[0].Layouts > 0);
        Assert.Equal(0, probes[500].Layouts); // far off-screen: never measured/laid out
    }

    [Fact]
    public void VariableList_UsesPrefixOffsets_AndWindows()
    {
        float H(int i)
        {
            return 10f + i % 5 * 10f; // 10,20,30,40,50 repeating
        }

        var probes = Enumerable.Range(0, 100).Select(i => new Probe(H(i))).ToList();
        var lv = new ListView {
            Smooth = false,
            HeightOf = H,
        };
        lv.SetItems(probes);

        lv.Measure(new Constraints(maxWidth: 200, maxHeight: 120));
        lv.Layout(Offset.Zero);

        Assert.Equal(0f, probes[0].Bounds.Y, 1); // top = 0
        Assert.Equal(10f, probes[1].Bounds.Y, 1); // + H(0)=10
        Assert.Equal(30f, probes[2].Bounds.Y, 1); // + H(1)=20
        Assert.Equal(60f, probes[3].Bounds.Y, 1); // + H(2)=30
        Assert.Equal(0, probes[90].Layouts); // off-screen
    }

    [Fact]
    public void Scroll_ShiftsWindowDown()
    {
        var probes = Enumerable.Range(0, 1000).Select(_ => new Probe(20f)).ToList();
        var lv = new ListView {
            ItemHeight = 20f,
            Smooth = false,
            ScrollSpeed = 1f,
        };
        lv.SetItems(probes);

        lv.Measure(new Constraints(maxWidth: 300, maxHeight: 200));
        lv.Layout(Offset.Zero);
        Assert.True(probes[0].Layouts > 0);
        Assert.Equal(0, probes[25].Layouts); // initially out of window

        lv.OnScroll(0, -400); // MoveBy(+400): jump down 20 rows (Smooth off ⇒ instant)
        lv.Measure(new Constraints(maxWidth: 300, maxHeight: 200));
        lv.Layout(Offset.Zero);

        Assert.True(probes[25].Layouts > 0); // window moved to ~rows 20-31
    }

    private static int TextCommands(PaintList p)
    {
        return p.DebugCommands.Count(c => (PaintCommandKind)c.Kind == PaintCommandKind.Text);
    }

    [Fact]
    public void TreeView_PaintsOnlyRowsInClipWindow()
    {
        var roots = Enumerable.Range(0, 1000).ToList();
        var tree = new TreeView<int>(roots, _ => [], i => i.ToString()) { RowHeight = 24f };

        // Unbounded height (as a ScrollView gives it) → Bounds spans all rows; an external clip windows it.
        tree.Measure(new Constraints(maxWidth: 300));
        tree.Layout(Offset.Zero);

        var top = new PaintList();
        top.AddClipStart(
            new Rect(
                0,
                0,
                300,
                100
            )
        ); // 100px ≈ 4-5 rows of 24px
        tree.Paint(top);
        top.AddClipEnd();
        Assert.InRange(TextCommands(top), 1, 12); // a handful, not 1000

        // Same small count when the window sits deep in the list (scrolled).
        var mid = new PaintList();
        mid.AddClipStart(
            new Rect(
                0,
                480,
                300,
                100
            )
        ); // rows ~20-24
        tree.Paint(mid);
        mid.AddClipEnd();
        Assert.InRange(TextCommands(mid), 1, 12);
    }

    // A leaf that records how many times it was laid out — so a test can assert off-screen rows are
    // never positioned.
    private sealed class Probe(float h) : Widget
    {
        private readonly float _h = h;
        public int Layouts;

        public override Size Measure(Constraints c)
        {
            return new Size(float.IsFinite(c.MaxWidth) ? c.MaxWidth : 100f, _h);
        }

        public override void Layout(Offset origin)
        {
            Layouts++;
            Bounds = new Rect(
                origin.X,
                origin.Y,
                100f,
                _h
            );
        }

        public override void Paint(PaintList paint)
        {
        }
    }
}
