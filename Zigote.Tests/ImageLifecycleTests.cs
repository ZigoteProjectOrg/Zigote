using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Tests;

/// <summary>
///     <see cref="Image" />'s ownership contract — the half that does not need a GPU. A texture
///     handle is owned by the widget: swapping one in releases the old, disposing releases the
///     current, and neither may leave a stale handle in the paint stream. (The engine half —
///     zigote_release_texture actually freeing CPU + GPU memory — needs a live device and is
///     covered by <c>ZIGOTE_SMOKE_TEXTURES=1 dotnet run --project Zigote.SmokeTest</c>.)
///     Handles here are opaque non-zero integers: with no engine instance the release calls are
///     no-ops by design, which is itself part of the contract — images are disposed during
///     teardown, after the engine is gone.
/// </summary>
public class ImageLifecycleTests
{
    private static PaintList PaintOf(Widget w)
    {
        var paint = new PaintList();
        w.Paint(paint);
        return paint;
    }

    private static Image LaidOut(Image image, float maxW = 400, float maxH = 400)
    {
        image.Measure(
            new Constraints(
                0,
                maxW,
                0,
                maxH
            )
        );
        image.Layout(Offset.Zero);
        return image;
    }

    [Fact]
    public void EmptyImage_MeasuresToZero_AndPaintsNothing()
    {
        var image = LaidOut(new Image());

        Assert.False(image.HasTexture);
        Assert.Equal(0, image.TextureBytes);
        Assert.Equal(0f, image.Bounds.Width);
        Assert.Equal(0, PaintOf(image).Count);
    }

    [Fact]
    public void PlaceholderSize_HoldsTheSlot_UntilTheTextureArrives()
    {
        // The point of the placeholder: a list row must not resize (and yank the scroll position)
        // when an async load lands.
        var image = new Image { PlaceholderSize = new Size(180, 260) };
        LaidOut(image);
        Assert.Equal(180f, image.Bounds.Width, 1);
        Assert.Equal(260f, image.Bounds.Height, 1);

        image.SetTexture(1, 100, 200);
        LaidOut(image);
        Assert.Equal(100f, image.Bounds.Width, 1);
        Assert.Equal(200f, image.Bounds.Height, 1);
    }

    [Fact]
    public void SetTexture_PaintsTheHandle_AndReportsItsFootprint()
    {
        var image = LaidOut(new Image());
        image.SetTexture(42, 64, 32);
        LaidOut(image);

        Assert.True(image.HasTexture);
        Assert.Equal((64u, 32u), image.TextureSize);
        Assert.Equal(64L * 32 * 4, image.TextureBytes);

        var cmd = Assert.Single(PaintOf(image).DebugCommands);
        Assert.Equal(64u, cmd.ImgPixelW);
        Assert.Equal(32u, cmd.ImgPixelH);
    }

    [Fact]
    public void Measure_FitsWithinConstraints_PreservingAspect()
    {
        var image = new Image();
        image.SetTexture(7, 1000, 500); // 2:1
        LaidOut(image, 400, 400);

        Assert.Equal(400f, image.Bounds.Width, 1);
        Assert.Equal(200f, image.Bounds.Height, 1);
    }

    [Fact]
    public void Dispose_ReleasesTheTexture_AndStopsPainting()
    {
        var image = new Image();
        image.SetTexture(9, 16, 16);
        LaidOut(image);
        Assert.Equal(1, PaintOf(image).Count);

        image.Dispose();
        LaidOut(image);

        Assert.False(image.HasTexture);
        Assert.Equal(0, image.TextureBytes);
        Assert.Equal(0, PaintOf(image).Count);

        image.Dispose(); // idempotent — double dispose must not double-release
        Assert.False(image.HasTexture);
    }

    [Fact]
    public void ClearTexture_IsANoOp_WhenEmpty()
    {
        var image = new Image();
        image.ClearTexture();
        image.ClearTexture();
        Assert.False(image.HasTexture);
    }

    [Fact]
    public void SetTexture_WithTheSameHandle_KeepsIt()
    {
        // Re-setting the live handle must not release-then-adopt the very texture it is keeping.
        var image = new Image();
        image.SetTexture(5, 8, 8);
        image.SetTexture(5, 8, 8);

        Assert.True(image.HasTexture);
        Assert.Equal((8u, 8u), image.TextureSize);
    }

    [Fact]
    public async Task LoadAsync_AfterDispose_DoesNothing()
    {
        var image = new Image();
        image.Dispose();

        var ran = false;
        await image.LoadAsync(_ =>
            {
                ran = true;
                return Task.FromResult(Array.Empty<byte>());
            }
        );

        Assert.False(ran);
        Assert.False(image.HasTexture);
    }
}