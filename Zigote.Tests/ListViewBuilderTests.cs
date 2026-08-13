using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Builder mode virtualizes construction as well as layout: only the rows in the viewport
///     window (plus a small overscan) are ever built, and rows that scroll away are dropped.
/// </summary>
public class ListViewBuilderTests
{
    [Fact]
    public void Builder_BuildsOnlyTheVisibleWindow()
    {
        int built = 0;
        var list = ListView.Builder(
            itemCount: 1_000_000,
            itemBuilder: _ =>
            {
                built++;
                return new FakeRow();
            },
            itemExtent: 20d
        );
        list.Smooth = false;

        list.Measure(Constraints.Tight(width: 200f, height: 100f));
        list.Layout(Offset.Zero);

        // 100 px viewport / 20 px rows = 5 visible, +1 slack row.
        Assert.InRange(actual: built, low: 5, high: 8);
        Assert.Equal(expected: 1_000_000, actual: list.Count);
        Assert.True(list.MaxScrollExtentY > 0f);
    }

    [Fact]
    public void ScrolledAwayRows_AreDropped()
    {
        var list = ListView.Builder(
            itemCount: 10_000,
            itemBuilder: _ => new FakeRow(),
            itemExtent: 20d
        );
        list.Smooth = false;

        for (int i = 0; i < 20; i++)
        {
            list.OnScroll(dx: 0f, dy: -25f); // 25 ticks × 40 px
            list.Measure(Constraints.Tight(width: 200f, height: 100f));
            list.Layout(Offset.Zero);
        }

        // Visible window + overscan on both sides — not a growing cache of every row seen.
        Assert.InRange(actual: list.GetChildren().Count(), low: 1, high: 20);
    }

    [Fact]
    public void EnsureVisible_ScrollsToARowOutsideTheWindow()
    {
        var list = ListView.Builder(
            itemCount: 1000,
            itemBuilder: _ => new FakeRow(),
            itemExtent: 20d
        );
        list.Smooth = false;

        list.Measure(Constraints.Tight(width: 200f, height: 100f));
        list.Layout(Offset.Zero);
        Assert.Equal(expected: 0f, actual: list.OffsetY);

        // Row 500 lives at 10 000 px — reveal applies at the next layout, once the extent is known.
        list.EnsureVisible(index: 500, margin: 0f);
        list.Measure(Constraints.Tight(width: 200f, height: 100f));
        list.Layout(Offset.Zero);

        Assert.InRange(actual: list.OffsetY, low: 10_000f - 100f, high: 10_000f);
    }

    [Fact]
    public void GridBuilder_VirtualizesRows_AndSizesCellsToWidth()
    {
        int built = 0;
        var grid = GridView.Builder(
            crossAxisCount: 4,
            itemCount: 10_000,
            itemBuilder: _ =>
            {
                built++;
                return new FakeRow();
            },
            childAspectRatio: 1d
        );
        grid.Smooth = false;

        grid.Measure(Constraints.Tight(width: 400f, height: 200f)); // 100 px cells → 2 rows visible
        grid.Layout(Offset.Zero);

        Assert.Equal(expected: 2500, actual: grid.Count);
        Assert.InRange(actual: built, low: 4, high: 4 * 8); // a few rows of 4 cells, not 10 000
    }

    private sealed class FakeRow : Widget
    {
        public override Size Measure(Constraints c) => new(width: 50f, height: 20f);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: 50f,
                height: 20f
            );
        }

        public override void Paint(PaintList paint) { }
    }
}
