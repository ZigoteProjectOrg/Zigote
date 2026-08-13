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
        var a = new SizedBox(width: 40, height: 10);
        var b = new SizedBox(width: 60, height: 20);
        var row = new Row(children: [a, b], spacing: 10f) { MainAxisSize = MainAxisSize.Min };

        var size = row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(x: 0, y: 0));

        Assert.Equal(expected: 110f, actual: size.Width, precision: 3); // 40 + 10 + 60
        Assert.Equal(expected: 0f, actual: a.Bounds.X, precision: 3);
        Assert.Equal(expected: 50f, actual: b.Bounds.X, precision: 3); // 40 + the 10px gap
    }

    [Fact]
    public void Column_Spacing_SeparatesFixedChildren_AndCountsTowardMinSize()
    {
        var a = new SizedBox(width: 10, height: 40);
        var b = new SizedBox(width: 20, height: 60);
        var col = new Column(children: [a, b], spacing: 8f) { MainAxisSize = MainAxisSize.Min };

        var size = col.Measure(new Constraints(maxWidth: 100, maxHeight: 200));
        col.Layout(new Offset(x: 0, y: 0));

        Assert.Equal(expected: 108f, actual: size.Height, precision: 3); // 40 + 8 + 60
        Assert.Equal(expected: 0f, actual: a.Bounds.Y, precision: 3);
        Assert.Equal(expected: 48f, actual: b.Bounds.Y, precision: 3);
    }

    [Fact]
    public void Row_Spacing_IsSubtractedBeforeFlexDistribution()
    {
        var fixedChild = new SizedBox(width: 40, height: 10);
        var flexChild = new SizedBox(width: 0, height: 10);
        var row = new Row(children: [fixedChild, new Expanded(flexChild)], spacing: 10f);

        row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(x: 0, y: 0));

        Assert.Equal(
            expected: 150f,
            actual: flexChild.Bounds.Width,
            precision: 2
        ); // 200 − 40 fixed − 10 gap
        Assert.Equal(expected: 50f, actual: flexChild.Bounds.X, precision: 2);
    }

    [Fact]
    public void Row_Spacing_ComposesWithSpaceBetween()
    {
        // 40 + 60 children in a 200-wide row with a 10px gap → 90px of slack for the alignment.
        var a = new SizedBox(width: 40, height: 10);
        var b = new SizedBox(width: 60, height: 10);
        var row = new Row(
            children: [a, b],
            mainAxisAlignment: MainAxisAlignment.SpaceBetween,
            spacing: 10f
        );

        row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(x: 0, y: 0));

        Assert.Equal(expected: 0f, actual: a.Bounds.X, precision: 3);
        Assert.Equal(
            expected: 140f,
            actual: b.Bounds.X,
            precision: 3
        ); // 40 + 10 gap + 90 slack — flush with the right edge
    }

    [Fact]
    public void Row_Spacing_WithEndAlignment_PacksChildrenToTheRight()
    {
        var a = new SizedBox(width: 40, height: 10);
        var b = new SizedBox(width: 60, height: 10);
        var row = new Row(children: [a, b], mainAxisAlignment: MainAxisAlignment.End, spacing: 10f);

        row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(x: 0, y: 0));

        Assert.Equal(expected: 90f, actual: a.Bounds.X, precision: 3);
        Assert.Equal(expected: 140f, actual: b.Bounds.X, precision: 3); // 90 + 40 + the 10px gap
    }

    [Fact]
    public void Spacing_AddsNothing_ForZeroOrOneChild()
    {
        var lone = new SizedBox(width: 40, height: 10);
        var row = new Row(children: [lone], spacing: 10f) { MainAxisSize = MainAxisSize.Min };
        Assert.Equal(
            expected: 40f,
            actual: row.Measure(new Constraints(maxWidth: 200, maxHeight: 100)).Width,
            precision: 3
        );

        var empty = new Column(spacing: 10f) { MainAxisSize = MainAxisSize.Min };
        Assert.Equal(
            expected: 0f,
            actual: empty.Measure(new Constraints(maxWidth: 200, maxHeight: 100)).Height,
            precision: 3
        );
    }
}
