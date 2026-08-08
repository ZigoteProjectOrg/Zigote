using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Exercises the Row/Column <c>Spacing</c> gap: it counts toward the measured main extent, is
///     subtracted from the space handed to flex children, and composes with
///     <c>MainAxisAlignment</c> (spacing sits between children; alignment distributes only the
///     remaining slack).
/// </summary>
public class FlexSpacingTests
{
    [Fact]
    public void Row_Spacing_SeparatesFixedChildren_AndCountsTowardMinSize()
    {
        var a = new SizedBox(40, 10);
        var b = new SizedBox(60, 20);
        var row = new Row([a, b], spacing: 10f) { MainAxisSize = MainAxisSize.Min };

        var size = row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(0, 0));

        Assert.Equal(110f, size.Width, 3); // 40 + 10 + 60
        Assert.Equal(0f, a.Bounds.X, 3);
        Assert.Equal(50f, b.Bounds.X, 3); // 40 + the 10px gap
    }

    [Fact]
    public void Column_Spacing_SeparatesFixedChildren_AndCountsTowardMinSize()
    {
        var a = new SizedBox(10, 40);
        var b = new SizedBox(20, 60);
        var col = new Column([a, b], spacing: 8f) { MainAxisSize = MainAxisSize.Min };

        var size = col.Measure(new Constraints(maxWidth: 100, maxHeight: 200));
        col.Layout(new Offset(0, 0));

        Assert.Equal(108f, size.Height, 3); // 40 + 8 + 60
        Assert.Equal(0f, a.Bounds.Y, 3);
        Assert.Equal(48f, b.Bounds.Y, 3);
    }

    [Fact]
    public void Row_Spacing_IsSubtractedBeforeFlexDistribution()
    {
        var fixedChild = new SizedBox(40, 10);
        var flexChild = new SizedBox(0, 10);
        var row = new Row([fixedChild, new Expanded(flexChild)], spacing: 10f);

        row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(0, 0));

        Assert.Equal(150f, flexChild.Bounds.Width, 2); // 200 − 40 fixed − 10 gap
        Assert.Equal(50f, flexChild.Bounds.X, 2);
    }

    [Fact]
    public void Row_Spacing_ComposesWithSpaceBetween()
    {
        // 40 + 60 children in a 200-wide row with a 10px gap → 90px of slack for the alignment.
        var a = new SizedBox(40, 10);
        var b = new SizedBox(60, 10);
        var row = new Row(
            [a, b],
            MainAxisAlignment.SpaceBetween,
            spacing: 10f
        );

        row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(0, 0));

        Assert.Equal(0f, a.Bounds.X, 3);
        Assert.Equal(140f, b.Bounds.X, 3); // 40 + 10 gap + 90 slack — flush with the right edge
    }

    [Fact]
    public void Row_Spacing_WithEndAlignment_PacksChildrenToTheRight()
    {
        var a = new SizedBox(40, 10);
        var b = new SizedBox(60, 10);
        var row = new Row([a, b], MainAxisAlignment.End, spacing: 10f);

        row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(0, 0));

        Assert.Equal(90f, a.Bounds.X, 3);
        Assert.Equal(140f, b.Bounds.X, 3); // 90 + 40 + the 10px gap
    }

    [Fact]
    public void Spacing_AddsNothing_ForZeroOrOneChild()
    {
        var lone = new SizedBox(40, 10);
        var row = new Row([lone], spacing: 10f) { MainAxisSize = MainAxisSize.Min };
        Assert.Equal(40f, row.Measure(new Constraints(maxWidth: 200, maxHeight: 100)).Width, 3);

        var empty = new Column(spacing: 10f) { MainAxisSize = MainAxisSize.Min };
        Assert.Equal(0f, empty.Measure(new Constraints(maxWidth: 200, maxHeight: 100)).Height, 3);
    }
}