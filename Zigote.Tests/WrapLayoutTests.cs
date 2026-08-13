using Xunit;
using Zigote.Core;
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
}
