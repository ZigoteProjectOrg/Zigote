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
        root.AddRect(new Rect(0, 0, 10, 10), Color.White);
        root.AddText("root", 0, 8, Color.Black, 12);

        var overlay = new PaintList();
        overlay.AddText("overlay", 0, 8, Color.Black, 12);
        overlay.AddImage(new Rect(0, 0, 2, 2), 2, 2, new byte[16]);

        var composite = new PaintList();
        composite.AppendFrom(root);
        composite.AppendFrom(overlay);

        Assert.Equal(4, composite.Count);
        // Blob lookups land on the re-based command indices: root text at 1, overlay text at 2,
        // overlay pixels at 3 — and nothing where no blob belongs.
        Assert.NotNull(composite.FindTextBlob(1));
        Assert.NotNull(composite.FindTextBlob(2));
        Assert.NotEqual(composite.FindTextBlob(1), composite.FindTextBlob(2));
        Assert.NotNull(composite.FindPixelBlob(3));
        Assert.Null(composite.FindTextBlob(0));
    }
}
