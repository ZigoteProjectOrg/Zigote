using PaintCommandKind = Zigote.Core.Native.ZgPaintOp;
using System.Linq;
using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Tests;

/// <summary>
///     The loading placeholder: it fills the width it is given unless told otherwise, it never
///     eats a pointer, and the sweep is clipped to its own rounded rect and leaves the block
///     alone when animation is switched off (reduced motion, screenshot tests).
/// </summary>
[Collection("Ticker")] // the shimmer owns a ticker; AdvanceAll is shared process-wide
public class SkeletonTests
{
    private static readonly Constraints Room = new(
        minWidth: 0f,
        maxWidth: 200f,
        minHeight: 0f,
        maxHeight: 200f
    );

    private static PaintList Render(Skeleton skeleton)
    {
        skeleton.Attach(owner: null!, parent: null);
        skeleton.Measure(Room);
        skeleton.Layout(Offset.Zero);
        var paint = new PaintList();
        skeleton.Paint(paint);
        return paint;
    }

    [Fact]
    public void FillsTheConstraintUnlessGivenAWidth()
    {
        Assert.Equal(expected: 200f, actual: new Skeleton().Measure(Room).Width);
        Assert.Equal(expected: 64f, actual: new Skeleton(width: 64f).Measure(Room).Width);

        var circle = Skeleton.Circle(48f);
        var size = circle.Measure(Room);
        Assert.Equal(expected: 48f, actual: size.Width);
        Assert.Equal(expected: 48f, actual: size.Height);
        Assert.Equal(expected: 24f, actual: circle.Radius);
    }

    [Theory]
    [InlineData(0f)] // band fully off the left edge
    [InlineData(1f)] // fully off the right edge
    public void SweepIsOffTheBlockAtBothEndsOfTheLoop(float phase)
    {
        var bounds = new Rect(
            x: 0f,
            y: 0f,
            width: 100f,
            height: 14f
        );
        Assert.All(
            collection: Enumerable.Range(start: 0, count: 16),
            action: i => Assert.Null(Skeleton.Slice(bounds: bounds, phase: phase, index: i))
        );
    }

    [Fact]
    public void SweepStaysInsideTheBlock_AndPeaksInTheMiddleOfTheBand()
    {
        var bounds = new Rect(
            x: 10f,
            y: 0f,
            width: 100f,
            height: 14f
        );
        var slices = Enumerable.Range(start: 0, count: 16)
            .Select(i => Skeleton.Slice(bounds: bounds, phase: 0.5f, index: i))
            .OfType<(float X, float Width, float Alpha)>()
            .ToList();

        Assert.NotEmpty(slices);
        Assert.All(
            collection: slices,
            action: s =>
            {
                Assert.InRange(actual: s.X, low: bounds.X, high: bounds.X + bounds.Width);
                Assert.InRange(actual: s.X + s.Width, low: bounds.X, high: bounds.X + bounds.Width);
                Assert.InRange(actual: s.Alpha, low: 0f, high: 1f);
            }
        );
        // Brightest slice sits in the middle of the band, not at either edge.
        int brightest = slices.IndexOf(slices.MaxBy(s => s.Alpha));
        Assert.InRange(actual: brightest, low: 1, high: slices.Count - 2);
    }

    [Fact]
    public void IsNotAPointerTarget()
    {
        var skeleton = new Skeleton(width: 50f);
        skeleton.Measure(Room);
        skeleton.Layout(Offset.Zero);
        Assert.Null(skeleton.HitTest(new Offset(x: 10f, y: 5f)));
    }

    [Fact]
    public void StaticSkeletonPaintsOneBlock_AnimatedOneAddsAClippedSweep()
    {
        var still = Render(new Skeleton(width: 100f) { Animated = false });
        var command = Assert.Single(still.DebugCommands);
        Assert.Equal(expected: PaintCommandKind.Rect, actual: command.Kind);

        var sweep = Render(new Skeleton(width: 100f));
        Assert.Contains(
            collection: sweep.DebugCommands,
            filter: c => c.Kind == PaintCommandKind.ClipStart
        );
        Assert.Contains(
            collection: sweep.DebugCommands,
            filter: c => c.Kind == PaintCommandKind.ClipEnd
        );
    }
}
