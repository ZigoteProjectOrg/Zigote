using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Shortest-column placement: tiles keep their own heights, columns stay balanced, and the
///     grid is as tall as its tallest column — not the sum of everything in it.
/// </summary>
public class StaggeredGridTests
{
    private static readonly Constraints Room = new(
        minWidth: 0f, maxWidth: 320f, minHeight: 0f, maxHeight: float.PositiveInfinity);

    private static SizedBox Tile(float height) => new(height: height);

    [Fact]
    public void ColumnWidthSplitsTheRowMinusTheGaps()
    {
        var grid = new StaggeredGrid(columns: 3, spacing: 10);
        // 320 - 2 gaps of 10 = 300, over three columns.
        Assert.Equal(expected: 100f, actual: grid.ColumnWidth(320f));
    }

    [Fact]
    public void TallestColumnSetsTheHeight_AndTilesGoToTheShortestColumn()
    {
        var grid = new StaggeredGrid(
            children: [Tile(100), Tile(40), Tile(30), Tile(30)],
            columns: 2, spacing: 20, runSpacing: 10);

        var size = grid.Measure(Room);
        grid.Layout(Offset.Zero);

        // Column 0: 100. Column 1: 40, then the next two tiles both land there (it stays the
        // shorter one) → 40 + 10 + 30 + 10 + 30 = 120.
        Assert.Equal(expected: 320f, actual: size.Width);
        Assert.Equal(expected: 120f, actual: size.Height);

        var children = grid.Children;
        Assert.Equal(expected: 0f, actual: children[0].Bounds.X);     // first column
        Assert.Equal(expected: 170f, actual: children[1].Bounds.X);   // 150 wide + 20 gap
        Assert.Equal(expected: 170f, actual: children[2].Bounds.X);
        Assert.Equal(expected: 50f, actual: children[2].Bounds.Y);    // under tile 1, plus the gap
        Assert.Equal(expected: 90f, actual: children[3].Bounds.Y);
    }

    [Fact]
    public void SingleColumnIsAStack_AndZeroColumnsIsNotAllowed()
    {
        var grid = new StaggeredGrid(children: [Tile(20), Tile(30)], columns: 0, runSpacing: 5);
        Assert.Equal(expected: 1, actual: grid.Columns);
        Assert.Equal(expected: 55f, actual: grid.Measure(Room).Height);
    }
}
