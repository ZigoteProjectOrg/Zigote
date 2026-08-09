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
        var built = 0;
        var list = ListView.Builder(
            1_000_000,
            _ =>
            {
                built++;
                return new FakeRow();
            },
            20d
        );
        list.Smooth = false;

        list.Measure(Constraints.Tight(200f, 100f));
        list.Layout(Offset.Zero);

        // 100 px viewport / 20 px rows = 5 visible, +1 slack row.
        Assert.InRange(built, 5, 8);
        Assert.Equal(1_000_000, list.Count);
        Assert.True(list.MaxScrollExtentY > 0f);
    }

    [Fact]
    public void ScrolledAwayRows_AreDropped()
    {
        var list = ListView.Builder(10_000, _ => new FakeRow(), 20d);
        list.Smooth = false;

        for (var i = 0; i < 20; i++)
        {
            list.OnScroll(0f, -25f); // 25 ticks × 40 px
            list.Measure(Constraints.Tight(200f, 100f));
            list.Layout(Offset.Zero);
        }

        // Visible window + overscan on both sides — not a growing cache of every row seen.
        Assert.InRange(list.GetChildren().Count(), 1, 20);
    }

    [Fact]
    public void EnsureVisible_ScrollsToARowOutsideTheWindow()
    {
        var list = ListView.Builder(1000, _ => new FakeRow(), 20d);
        list.Smooth = false;

        list.Measure(Constraints.Tight(200f, 100f));
        list.Layout(Offset.Zero);
        Assert.Equal(0f, list.OffsetY);

        // Row 500 lives at 10 000 px — reveal applies at the next layout, once the extent is known.
        list.EnsureVisible(500, 0f);
        list.Measure(Constraints.Tight(200f, 100f));
        list.Layout(Offset.Zero);

        Assert.InRange(list.OffsetY, 10_000f - 100f, 10_000f);
    }

    [Fact]
    public void GridBuilder_VirtualizesRows_AndSizesCellsToWidth()
    {
        var built = 0;
        var grid = GridView.Builder(
            4,
            10_000,
            _ =>
            {
                built++;
                return new FakeRow();
            },
            childAspectRatio: 1d
        );
        grid.Smooth = false;

        grid.Measure(Constraints.Tight(400f, 200f)); // 100 px cells → 2 rows visible
        grid.Layout(Offset.Zero);

        Assert.Equal(2500, grid.Count);
        Assert.InRange(built, 4, 4 * 8); // a few rows of 4 cells, not 10 000
    }

    private sealed class FakeRow : Widget
    {
        public override Size Measure(Constraints c)
        {
            return new Size(50f, 20f);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                50f,
                20f
            );
        }

        public override void Paint(PaintList paint)
        {
        }
    }
}