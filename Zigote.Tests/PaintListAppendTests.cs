using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.Tests;

/// <summary>
///     <see cref="PaintList.AppendFrom" /> exists for one caller — the capture path compositing the
///     root and overlay layers into the single list <c>CaptureUiBmp</c> renders — and its one tricky
///     part is re-basing the sparse blob-index tables. Get that wrong and an overlay's text draws with
///     the root's string (or none), which is exactly the class of bug byte-diff streaming can't catch.
/// </summary>
public class PaintListAppendTests
{
    [Fact]
    public void AppendFrom_rebases_blob_indices_onto_the_composite()
    {
        var root = new PaintList();
        root.AddRect(
            bounds: new Rect(
                x: 0,
                y: 0,
                width: 10,
                height: 10
            ),
            color: Color.White
        );
        root.AddText(
            text: "root",
            baselineX: 0,
            baselineY: 8,
            color: Color.Black,
            fontSize: 12
        );

        var overlay = new PaintList();
        overlay.AddText(
            text: "overlay",
            baselineX: 0,
            baselineY: 8,
            color: Color.Black,
            fontSize: 12
        );
        overlay.AddImage(
            bounds: new Rect(
                x: 0,
                y: 0,
                width: 2,
                height: 2
            ),
            pixelWidth: 2,
            pixelHeight: 2,
            pixels: new byte[16]
        );

        var composite = new PaintList();
        composite.AppendFrom(root);
        composite.AppendFrom(overlay);

        Assert.Equal(expected: 4, actual: composite.Count);
        // Blob lookups land on the re-based command indices: root text at 1, overlay text at 2,
        // overlay pixels at 3 — and nothing where no blob belongs.
        Assert.NotNull(composite.FindTextBlob(1));
        Assert.NotNull(composite.FindTextBlob(2));
        Assert.NotEqual(expected: composite.FindTextBlob(1), actual: composite.FindTextBlob(2));
        Assert.NotNull(composite.FindPixelBlob(3));
        Assert.Null(composite.FindTextBlob(0));
    }
}
