using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.Tests;

/// <summary>
///     The renderer keeps the pixel pointer a submitted image record carries and reads it when it
///     draws the frame, after the submit call has returned. The array therefore has to stay pinned
///     until the list is cleared or submitted again — released at the end of the submit, a
///     compacting GC moved it and the renderer read whatever was at the old address.
/// </summary>
public class PaintListPinTests
{
    private static unsafe int Submit(PaintList paint)
    {
        int calls = 0;
        paint.PinAndCall((_, _) => calls++);
        return calls;
    }

    private static PaintList WithInlineImage()
    {
        var paint = new PaintList();
        paint.AddImage(
            bounds: new Rect(x: 0, y: 0, width: 2, height: 2),
            pixelWidth: 2,
            pixelHeight: 2,
            pixels: new byte[16]
        );
        return paint;
    }

    [Fact]
    public void InlinePixels_StayPinnedAfterTheSubmitReturns()
    {
        var paint = WithInlineImage();
        Assert.Equal(expected: 1, actual: Submit(paint));
        Assert.Equal(expected: 1, actual: paint.PinnedPixelBlobs);
    }

    [Fact]
    public void Pins_AreReleasedByClear_AndNotAccumulatedByResubmitting()
    {
        var paint = WithInlineImage();
        Submit(paint);
        Submit(paint);
        Assert.Equal(expected: 1, actual: paint.PinnedPixelBlobs);

        paint.Clear();
        Assert.Equal(expected: 0, actual: paint.PinnedPixelBlobs);
    }
}
