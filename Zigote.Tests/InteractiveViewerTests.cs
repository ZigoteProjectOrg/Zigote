using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="InteractiveViewer" />'s transform contract, which is all geometry and therefore
///     testable without a window: a zoom holds the point it was aimed at, a pinch out-and-back
///     lands where it started, the content never leaves the viewport while constrained, and the
///     gesture opt-ins say no while there is nothing to pan (so the enclosing scroller keeps the
///     wheel and the finger).
/// </summary>
public class InteractiveViewerTests
{
    private const float W = 400f;
    private const float H = 300f;

    /// <summary>
    ///     Where a content point lands on screen — the same mapping Paint pushes:
    ///     screen = viewportTopLeft + translation + scale × point, with the viewport at the origin
    ///     throughout these tests.
    /// </summary>
    private static Offset Project(InteractiveViewer viewer, Offset contentPoint)
    {
        var t = viewer.Translation;
        return new Offset(
            t.X + viewer.Scale * contentPoint.X,
            t.Y + viewer.Scale * contentPoint.Y
        );
    }

    private static InteractiveViewer LaidOut(InteractiveViewer viewer, float w = W, float h = H)
    {
        viewer.Measure(Constraints.Tight(w, h));
        viewer.Layout(Offset.Zero);
        return viewer;
    }

    private static InteractiveViewer Viewer()
    {
        return LaidOut(new InteractiveViewer(new SizedBox(W, H)) { MaxScale = 8f });
    }

    [Fact]
    public void StartsAtRest()
    {
        var viewer = Viewer();

        Assert.Equal(1f, viewer.Scale, 4);
        Assert.False(viewer.IsTransformed);
    }

    [Fact]
    public void ZoomHoldsTheFocalPoint()
    {
        var viewer = Viewer();
        var focus = new Offset(100f, 90f);

        // The content point under the focus before the zoom must still be under it after, or the
        // picture slides out from under the fingers.
        viewer.OnTouchScale(2f, focus);

        Assert.Equal(2f, viewer.Scale, 4);
        var landed = Project(viewer, new Offset(100f, 90f)); // content point == focus at scale 1
        Assert.Equal(focus.X, landed.X, 2);
        Assert.Equal(focus.Y, landed.Y, 2);
    }

    [Fact]
    public void SqueezeUndoesSpread()
    {
        var viewer = Viewer();
        var focus = new Offset(310f, 40f);

        viewer.OnTouchScale(2.5f, focus);
        viewer.OnTouchScale(1f / 2.5f, focus);

        Assert.Equal(1f, viewer.Scale, 3);
        Assert.False(viewer.IsTransformed); // and back to exactly where it started
    }

    [Fact]
    public void ScaleIsClampedToTheRange()
    {
        var viewer = LaidOut(
            new InteractiveViewer(new SizedBox(W, H)) { MinScale = 1f, MaxScale = 3f }
        );

        viewer.OnTouchScale(100f, new Offset(0f, 0f));
        Assert.Equal(3f, viewer.Scale, 4);

        viewer.OnTouchScale(0.001f, new Offset(0f, 0f));
        Assert.Equal(1f, viewer.Scale, 4);
    }

    [Fact]
    public void ConstrainedPanNeverUncoversTheViewport()
    {
        var viewer = Viewer();
        viewer.OnTouchScale(2f, new Offset(W / 2f, H / 2f));

        // Shove it far past every edge in turn; the content must still cover [0,W]×[0,H].
        foreach (var shove in new[] {
                     new Offset(9999f, 9999f),
                     new Offset(-99999f, -99999f),
                 })
        {
            viewer.OnTouchScroll(shove.X, shove.Y);

            var offset = viewer.Translation;
            var overflowX = W * viewer.Scale - W;
            var overflowY = H * viewer.Scale - H;
            Assert.InRange(offset.X, -overflowX - 0.01f, 0.01f);
            Assert.InRange(offset.Y, -overflowY - 0.01f, 0.01f);
        }
    }

    [Fact]
    public void UnconstrainedPanIsFree()
    {
        var viewer = LaidOut(
            new InteractiveViewer(new SizedBox(W, H)) { ConstrainToBounds = false }
        );

        viewer.OnTouchScroll(500f, -250f);

        var offset = viewer.Translation;
        Assert.Equal(500f, offset.X, 2);
        Assert.Equal(-250f, offset.Y, 2);
    }

    [Fact]
    public void TouchScrollDeclinedWhileNothingOverflows()
    {
        var viewer = Viewer();

        // At rest the content exactly covers the viewport: the finger belongs to whatever scrolls
        // around this viewer, not to it.
        Assert.False(viewer.CanTouchScroll(true));
        Assert.False(viewer.CanTouchScroll(false));

        viewer.OnTouchScale(2f, new Offset(W / 2f, H / 2f));
        Assert.True(viewer.CanTouchScroll(true));
        Assert.True(viewer.CanTouchScroll(false));
    }

    [Fact]
    public void PanDisabledDeclinesTheFinger()
    {
        var viewer = LaidOut(new InteractiveViewer(new SizedBox(W, H)) { PanEnabled = false });
        viewer.OnTouchScale(2f, new Offset(W / 2f, H / 2f));

        Assert.False(viewer.CanTouchScroll(true));
        Assert.True(viewer.CanTouchScale()); // pinch still zooms it
    }

    [Fact]
    public void ScaleDisabledDeclinesThePinch()
    {
        var viewer = LaidOut(new InteractiveViewer(new SizedBox(W, H)) { ScaleEnabled = false });

        Assert.False(viewer.CanTouchScale());
        viewer.OnTouchScale(3f, new Offset(0f, 0f)); // must not throw, must not zoom
        Assert.Equal(1f, viewer.Scale, 4);
    }

    [Fact]
    public void ResetReturnsToRest()
    {
        var viewer = Viewer();
        viewer.OnTouchScale(4f, new Offset(20f, 20f));
        viewer.OnTouchScroll(-60f, -40f);
        Assert.True(viewer.IsTransformed);

        viewer.Reset(false);

        Assert.Equal(1f, viewer.Scale, 4);
        Assert.False(viewer.IsTransformed);
    }

    [Fact]
    public void ZoomToIsAbsoluteAndFocused()
    {
        var viewer = Viewer();
        var focus = new Offset(50f, 250f);

        viewer.ZoomTo(3f, focus, false);

        Assert.Equal(3f, viewer.Scale, 4);
        var landed = Project(viewer, focus);
        Assert.Equal(focus.X, landed.X, 2);
        Assert.Equal(focus.Y, landed.Y, 2);
    }

    [Fact]
    public void ContentSmallerThanTheViewportIsCentred()
    {
        // MinScale below 1 means the content can be smaller than its box; constrained, it centres
        // rather than sticking to a corner.
        var viewer = LaidOut(new InteractiveViewer(new SizedBox(W, H)) { MinScale = 0.5f });
        viewer.ZoomTo(0.5f, new Offset(0f, 0f), false);

        var offset = viewer.Translation;
        Assert.Equal(W * 0.25f, offset.X, 2); // (W − W×0.5) / 2
        Assert.Equal(H * 0.25f, offset.Y, 2);
    }

    [Fact]
    public void ResizeReclampsInsteadOfStrandingTheContent()
    {
        var viewer = Viewer();
        viewer.OnTouchScale(4f, new Offset(W, H)); // pinned to the bottom-right corner
        viewer.OnTouchScroll(-9999f, -9999f);

        // The window gets wider: the old offset is now past the edge and must be pulled back.
        LaidOut(viewer, W * 2f);

        var offset = viewer.Translation;
        var overflowX = W * 2f * viewer.Scale - W * 2f;
        Assert.InRange(offset.X, -overflowX - 0.01f, 0.01f);
    }
}
