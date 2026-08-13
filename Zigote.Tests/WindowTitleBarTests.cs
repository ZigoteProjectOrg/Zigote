using Xunit;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     The Adwaita headerbar hosts the app menu (<see cref="WindowTitleBar.Leading" />) on the same
///     row as the centred title and the window buttons. As the window narrows, the gap left for the
///     title shrinks and eventually inverts — which is exactly what crashed a live resize (Math.Clamp
///     on min &gt; max). These pin that the bar paints at any width.
/// </summary>
public class WindowTitleBarTests
{
    private static WindowTitleBar Bar(float leadingWidth)
    {
        return new WindowTitleBar {
            Title = "Zigote Editor",
            Style = WindowChromeStyle.AdwaitaCsd,
            Leading = new SizedBox(width: leadingWidth, height: WindowTitleBar.BarHeight),
        };
    }

    [Theory]
    // Wide: title fits between the menu and the buttons. Narrow: it does not, and the shrinking
    // window walks the free gap down through zero and negative.
    [InlineData(1280f)]
    [InlineData(400f)]
    [InlineData(200f)]
    [InlineData(120f)]
    [InlineData(40f)]
    public void PaintsAtAnyWidth(float width)
    {
        var bar = Bar(130f); // a File/Edit/Help-sized menu strip
        bar.Measure(new Constraints(maxWidth: width, maxHeight: WindowTitleBar.BarHeight));
        bar.Layout(new Offset(x: 0f, y: 0f));
        bar.Paint(new PaintList()); // threw ArgumentException below ~132px before the guard moved
    }

    [Fact]
    public void LeadingIsLaidOutAtTheLeftAndNeverExceedsTheBar()
    {
        var lead = new SizedBox(width: 130f, height: WindowTitleBar.BarHeight);
        var bar = new WindowTitleBar {
            Title = "Zigote Editor",
            Style = WindowChromeStyle.AdwaitaCsd,
            Leading = lead,
        };

        bar.Measure(new Constraints(maxWidth: 800f, maxHeight: WindowTitleBar.BarHeight));
        bar.Layout(new Offset(x: 0f, y: 0f));

        Assert.Equal(
            expected: 0f,
            actual: lead.Bounds.X,
            precision: 3
        ); // flush left — no traffic-light inset off macOS
        Assert.Equal(expected: 0f, actual: lead.Bounds.Y, precision: 3);
        Assert.True(lead.Bounds.Right <= bar.Bounds.Right);
    }
}
