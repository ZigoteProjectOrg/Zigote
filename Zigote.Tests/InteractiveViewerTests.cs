using Xunit;
using Zigote.Core;
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
            x: t.X + (viewer.Scale * contentPoint.X),
            y: t.Y + (viewer.Scale * contentPoint.Y)
        );
    }

    private static InteractiveViewer LaidOut(InteractiveViewer viewer, float w = W, float h = H)
    {
        viewer.Measure(Constraints.Tight(width: w, height: h));
        viewer.Layout(Offset.Zero);
        return viewer;
    }

    private static InteractiveViewer Viewer() => LaidOut(
        new InteractiveViewer(new SizedBox(width: W, height: H)) { MaxScale = 8f }
    );

    [Fact]
    public void StartsAtRest()
    {
        var viewer = Viewer();

        Assert.Equal(expected: 1f, actual: viewer.Scale, precision: 4);
        Assert.False(viewer.IsTransformed);
    }

    [Fact]
    public void ZoomHoldsTheFocalPoint()
    {
        var viewer = Viewer();
        var focus = new Offset(x: 100f, y: 90f);

        // The content point under the focus before the zoom must still be under it after, or the
        // picture slides out from under the fingers.
        viewer.OnTouchScale(scale: 2f, focus: focus);

        Assert.Equal(expected: 2f, actual: viewer.Scale, precision: 4);
        var landed = Project(
            viewer: viewer,
            contentPoint: new Offset(x: 100f, y: 90f)
        ); // content point == focus at scale 1
        Assert.Equal(expected: focus.X, actual: landed.X, precision: 2);
        Assert.Equal(expected: focus.Y, actual: landed.Y, precision: 2);
    }

    [Fact]
    public void SqueezeUndoesSpread()
    {
        var viewer = Viewer();
        var focus = new Offset(x: 310f, y: 40f);

        viewer.OnTouchScale(scale: 2.5f, focus: focus);
        viewer.OnTouchScale(scale: 1f / 2.5f, focus: focus);

        Assert.Equal(expected: 1f, actual: viewer.Scale, precision: 3);
        Assert.False(viewer.IsTransformed); // and back to exactly where it started
    }

    [Fact]
    public void ScaleIsClampedToTheRange()
    {
        var viewer = LaidOut(
            new InteractiveViewer(new SizedBox(width: W, height: H)) {
                MinScale = 1f,
                MaxScale = 3f,
            }
        );

        viewer.OnTouchScale(scale: 100f, focus: new Offset(x: 0f, y: 0f));
        Assert.Equal(expected: 3f, actual: viewer.Scale, precision: 4);

        viewer.OnTouchScale(scale: 0.001f, focus: new Offset(x: 0f, y: 0f));
        Assert.Equal(expected: 1f, actual: viewer.Scale, precision: 4);
    }

    [Fact]
    public void ConstrainedPanNeverUncoversTheViewport()
    {
        var viewer = Viewer();
        viewer.OnTouchScale(scale: 2f, focus: new Offset(x: W / 2f, y: H / 2f));

        // Shove it far past every edge in turn; the content must still cover [0,W]×[0,H].
        foreach (var shove in new[] {
                     new Offset(x: 9999f, y: 9999f),
                     new Offset(x: -99999f, y: -99999f),
                 })
        {
            viewer.OnTouchScroll(dx: shove.X, dy: shove.Y);

            var offset = viewer.Translation;
            float overflowX = (W * viewer.Scale) - W;
            float overflowY = (H * viewer.Scale) - H;
            Assert.InRange(actual: offset.X, low: -overflowX - 0.01f, high: 0.01f);
            Assert.InRange(actual: offset.Y, low: -overflowY - 0.01f, high: 0.01f);
        }
    }

    [Fact]
    public void UnconstrainedPanIsFree()
    {
        var viewer = LaidOut(
            new InteractiveViewer(new SizedBox(width: W, height: H)) { ConstrainToBounds = false }
        );

        viewer.OnTouchScroll(dx: 500f, dy: -250f);

        var offset = viewer.Translation;
        Assert.Equal(expected: 500f, actual: offset.X, precision: 2);
        Assert.Equal(expected: -250f, actual: offset.Y, precision: 2);
    }

    [Fact]
    public void TouchScrollDeclinedWhileNothingOverflows()
    {
        var viewer = Viewer();

        // At rest the content exactly covers the viewport: the finger belongs to whatever scrolls
        // around this viewer, not to it.
        Assert.False(viewer.CanTouchScroll(true));
        Assert.False(viewer.CanTouchScroll(false));

        viewer.OnTouchScale(scale: 2f, focus: new Offset(x: W / 2f, y: H / 2f));
        Assert.True(viewer.CanTouchScroll(true));
        Assert.True(viewer.CanTouchScroll(false));
    }

    [Fact]
    public void PanDisabledDeclinesTheFinger()
    {
        var viewer = LaidOut(
            new InteractiveViewer(new SizedBox(width: W, height: H)) { PanEnabled = false }
        );
        viewer.OnTouchScale(scale: 2f, focus: new Offset(x: W / 2f, y: H / 2f));

        Assert.False(viewer.CanTouchScroll(true));
        Assert.True(viewer.CanTouchScale()); // pinch still zooms it
    }

    [Fact]
    public void ScaleDisabledDeclinesThePinch()
    {
        var viewer = LaidOut(
            new InteractiveViewer(new SizedBox(width: W, height: H)) { ScaleEnabled = false }
        );

        Assert.False(viewer.CanTouchScale());
        viewer.OnTouchScale(
            scale: 3f,
            focus: new Offset(x: 0f, y: 0f)
        ); // must not throw, must not zoom
        Assert.Equal(expected: 1f, actual: viewer.Scale, precision: 4);
    }

    [Fact]
    public void ResetReturnsToRest()
    {
        var viewer = Viewer();
        viewer.OnTouchScale(scale: 4f, focus: new Offset(x: 20f, y: 20f));
        viewer.OnTouchScroll(dx: -60f, dy: -40f);
        Assert.True(viewer.IsTransformed);

        viewer.Reset(false);

        Assert.Equal(expected: 1f, actual: viewer.Scale, precision: 4);
        Assert.False(viewer.IsTransformed);
    }

    [Fact]
    public void ZoomToIsAbsoluteAndFocused()
    {
        var viewer = Viewer();
        var focus = new Offset(x: 50f, y: 250f);

        viewer.ZoomTo(scale: 3f, focus: focus, animate: false);

        Assert.Equal(expected: 3f, actual: viewer.Scale, precision: 4);
        var landed = Project(viewer: viewer, contentPoint: focus);
        Assert.Equal(expected: focus.X, actual: landed.X, precision: 2);
        Assert.Equal(expected: focus.Y, actual: landed.Y, precision: 2);
    }

    [Fact]
    public void ContentSmallerThanTheViewportIsCentred()
    {
        // MinScale below 1 means the content can be smaller than its box; constrained, it centres
        // rather than sticking to a corner.
        var viewer = LaidOut(
            new InteractiveViewer(new SizedBox(width: W, height: H)) { MinScale = 0.5f }
        );
        viewer.ZoomTo(scale: 0.5f, focus: new Offset(x: 0f, y: 0f), animate: false);

        var offset = viewer.Translation;
        Assert.Equal(expected: W * 0.25f, actual: offset.X, precision: 2); // (W − W×0.5) / 2
        Assert.Equal(expected: H * 0.25f, actual: offset.Y, precision: 2);
    }

    [Fact]
    public void ResizeReclampsInsteadOfStrandingTheContent()
    {
        var viewer = Viewer();
        viewer.OnTouchScale(
            scale: 4f,
            focus: new Offset(x: W, y: H)
        ); // pinned to the bottom-right corner
        viewer.OnTouchScroll(dx: -9999f, dy: -9999f);

        // The window gets wider: the old offset is now past the edge and must be pulled back.
        LaidOut(viewer: viewer, w: W * 2f);

        var offset = viewer.Translation;
        float overflowX = (W * 2f * viewer.Scale) - (W * 2f);
        Assert.InRange(actual: offset.X, low: -overflowX - 0.01f, high: 0.01f);
    }
}
