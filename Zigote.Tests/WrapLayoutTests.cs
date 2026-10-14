using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Wrap caches one offset per child during Measure and replays it in Layout. Anything that adds
///     children between the two passes — a reconcile, a subtree swapped in by an ancestor that then
///     skipped re-measuring, a re-entrant relayout from a live resize — used to walk that table off
///     its end and take the frame loop down with an IndexOutOfRangeException.
/// </summary>
public class WrapLayoutTests
{
    private static readonly Constraints Room = new(
        minWidth: 0f,
        maxWidth: 200f,
        minHeight: 0f,
        maxHeight: 200f
    );

    [Fact]
    public void LayoutSurvivesChildrenAddedAfterMeasure()
    {
        var wrap = new Wrap(spacing: 4, runSpacing: 4);
        wrap.Children.Add(new SizedBox(width: 50f, height: 20f));
        wrap.Children.Add(new SizedBox(width: 50f, height: 20f));
        wrap.Measure(Room);

        wrap.Children.Add(new SizedBox(width: 50f, height: 20f)); // no Measure in between
        wrap.Layout(Offset.Zero);

        // The measured prefix is placed, and the widget asked to be laid out again for the rest.
        Assert.Equal(expected: 0f, actual: wrap.Children[0].Bounds.X, precision: 3);
        Assert.True(wrap.NeedsLayout);
    }

    [Fact]
    public void LayoutBeforeAnyMeasureDoesNotThrow()
    {
        var wrap = new Wrap(spacing: 4, runSpacing: 4);
        wrap.Children.Add(new SizedBox(width: 50f, height: 20f));

        wrap.Layout(Offset.Zero); // freshly built subtree laid out before its first measure

        Assert.True(wrap.NeedsLayout);
    }

    [Fact]
    public void CenterAlignment_CentresShortChildrenWithinTheirOwnRun()
    {
        var wrap =
            new Wrap(spacing: 0, runSpacing: 0) { CrossAlignment = CrossAxisAlignment.Center };
        wrap.Children.Add(new SizedBox(width: 50f, height: 40f));
        wrap.Children.Add(new SizedBox(width: 50f, height: 20f)); // run 0: centred against 40
        wrap.Children.Add(new SizedBox(width: 50f, height: 10f)); // run 1: alone, so no shift
        wrap.Measure(new Constraints(maxWidth: 100f, maxHeight: 600f));
        wrap.Layout(Offset.Zero);

        Assert.Equal(expected: 0f, actual: wrap.Children[0].Bounds.Y, precision: 3);
        Assert.Equal(expected: 10f, actual: wrap.Children[1].Bounds.Y, precision: 3);
        Assert.Equal(expected: 40f, actual: wrap.Children[2].Bounds.Y, precision: 3);
    }
}
