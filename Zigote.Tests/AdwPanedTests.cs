using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="AdwPaned" /> divides a box between two children, and the interesting part is what
///     it refuses to do: starve a pane below <see cref="AdwPaned.MinPaneSize" />, or let a drag walk
///     the handle off either end. These drive it headlessly through a full drag.
/// </summary>
public class AdwPanedTests
{
    private const float W = 800f;
    private const float H = 600f;

    private static (AdwPaned Paned, SizedBox First, SizedBox Second) Laid(
        bool vertical, float position = 0.5f, float min = 180f)
    {
        var first = new SizedBox();
        var second = new SizedBox();
        var paned = new AdwPaned(first, second, vertical) {
            Position = position,
            MinPaneSize = min,
        };
        var wrapper = new ThemeProvider(ThemeData.Dark, paned);
        wrapper.Measure(Constraints.Tight(W, H));
        wrapper.Layout(new Offset(0f, 0f));
        return (paned, first, second);
    }

    [Fact]
    public void Horizontal_SplitsTheBoxAtThePositionAndLeavesNoGap()
    {
        var (paned, first, second) = Laid(false, 0.25f);

        Assert.Equal(0f, first.Bounds.X, 3);
        Assert.Equal(H, first.Bounds.Height, 3);
        // Handle gutter sits between the panes, and the three spans tile the box exactly.
        Assert.Equal(first.Bounds.Right + paned.HandleWidth, second.Bounds.X, 3);
        Assert.Equal(W, second.Bounds.Right, 3);
        // Floored, not rounded: panes land on whole pixels so the hairline handle never straddles
        // two of them. The remainder goes to the second pane, which is why the box still tiles.
        Assert.Equal(MathF.Floor((W - paned.HandleWidth) * 0.25f), first.Bounds.Width, 3);
    }

    [Fact]
    public void Vertical_SplitsTheOtherAxis()
    {
        var (paned, first, second) = Laid(true, 0.6f);

        Assert.Equal(W, first.Bounds.Width, 3);
        Assert.Equal(first.Bounds.Bottom + paned.HandleWidth, second.Bounds.Y, 3);
        Assert.Equal(H, second.Bounds.Bottom, 3);
        Assert.Equal(MathF.Floor((H - paned.HandleWidth) * 0.6f), first.Bounds.Height, 3);
    }

    [Fact]
    public void DraggingTheHandleMovesThePositionAndReportsItOnce()
    {
        var (paned, first, _) = Laid(false, 0.5f);
        var reported = new List<float>();
        paned.OnPositionChanged = p => reported.Add(p);

        var handleX = first.Bounds.Right + paned.HandleWidth / 2f;
        paned.OnPointerDown(new Offset(handleX, H / 2f));
        paned.OnPointerMove(new Offset(handleX - 100f, H / 2f));

        // Live during the drag, so panes track the pointer...
        Assert.True(paned.Position < 0.5f);
        Assert.Empty(reported);

        // ...but the persistence callback fires once, on release.
        paned.OnPointerUp(new Offset(handleX - 100f, H / 2f));
        Assert.Single(reported);
        Assert.Equal(paned.Position, reported[0], 4);
    }

    [Fact]
    public void ADragPastTheEndStopsAtTheMinimumPaneSize()
    {
        var (paned, first, second) = Laid(false, 0.5f, 180f);
        var handleX = first.Bounds.Right + paned.HandleWidth / 2f;

        paned.OnPointerDown(new Offset(handleX, H / 2f));
        paned.OnPointerMove(new Offset(-4000f, H / 2f)); // yank it far off the left edge

        // Re-lay out: the clamp lives in Layout, which is what a MarkNeedsLayout would run.
        var wrapper = new ThemeProvider(ThemeData.Dark, paned);
        wrapper.Measure(Constraints.Tight(W, H));
        wrapper.Layout(new Offset(0f, 0f));

        Assert.InRange(first.Bounds.Width, 179f, 181f);
        Assert.True(second.Bounds.Width >= 180f);
    }

    /// <summary>
    ///     A box too small to honour MinPaneSize twice must still split rather than collapse a pane
    ///     to nothing — the fallback band, not the min-derived one.
    /// </summary>
    [Fact]
    public void ABoxSmallerThanTwoMinimumsFallsBackToAProportionalSplit()
    {
        var first = new SizedBox();
        var second = new SizedBox();
        var paned = new AdwPaned(first, second) { Position = 0f, MinPaneSize = 180f };
        var wrapper = new ThemeProvider(ThemeData.Dark, paned);
        wrapper.Measure(Constraints.Tight(200f, 100f));
        wrapper.Layout(new Offset(0f, 0f));

        Assert.True(first.Bounds.Width > 0f);
        Assert.True(second.Bounds.Width > 0f);
        Assert.Equal(200f, second.Bounds.Right, 3);
    }
}
