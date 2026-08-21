using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Engine;
using Zigote.Core.Paint;

namespace WebView.Tests;

/// <summary>
///     The one thing no engine-less test can check: that the page's pixels reach the GPU with their
///     channels and rows in the right places. The frame path hands the engine Cairo's own BGRA rows
///     at Cairo's own stride — a channel-order or stride mistake there is invisible to every other
///     test and shows up on screen as a blue page, a sheared page, or a stale stripe.
///     <para>
///         Opens a real window (an engine needs one) and captures the 2D paint path with the
///         engine's own golden-image seam, so it is opt-in: <c>ZIGOTE_WEBVIEW_GPUCHECK=1</c>.
///     </para>
/// </summary>
public class TexturePathGpuCheck
{
    [Fact]
    public void PagePixelsReachTheGpuWithTheirChannelsAndRowsIntact()
    {
        if (Environment.GetEnvironmentVariable("ZIGOTE_WEBVIEW_GPUCHECK") is not { Length: > 0 }) return;

        using var engine = new ZigoteEngine();
        engine.Initialize(width: 640, height: 480, title: "webview texture check");

        using var controller = new WebViewController();
        controller.EnsureAttached(new NativeParent(NativeParentKind.Wayland, 0, 0));
        var backend = (WebKitOffscreenBackend)controller.TextureBackend!;
        backend.SetSurfaceSize(logicalWidth: 300, logicalHeight: 200, scale: 1f);
        // Left half a colour whose channels are all different (so a swap cannot hide), right half
        // its mirror, so a row-stride mistake shears the boundary instead of going unnoticed.
        controller.LoadHtml(
            "<body style='margin:0;display:flex;height:100vh'>" +
            "<div style='flex:1;background:rgb(32,64,128)'></div>" +
            "<div style='flex:1;background:rgb(128,64,32)'></div></body>");

        // Wait for the PAGE, not merely for a frame: the first frames a webview produces are the
        // blank white surface it starts on, and capturing one of those would "pass" against a
        // channel swap as happily as against a correct upload.
        ulong texture = 0;
        bool painted = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !painted)
        {
            Ticker.AdvanceAll(1f / 60f);
            texture = backend.AcquireTexture();
            if (texture != 0 && backend.TryCopyFrame(out byte[] rgba, out int fw, out _, out _))
            {
                // The left half's colour itself — white (the surface WebKit starts on) and black
                // (an allocated-but-unpainted surface) both have to fail this, or the capture below
                // would be taken before the page exists.
                int probe = (((fw / 4) + (fw * 10)) * 4);
                painted = probe + 3 < rgba.Length &&
                          Math.Abs(rgba[probe] - 32) <= 16 &&
                          Math.Abs(rgba[probe + 1] - 64) <= 16 &&
                          Math.Abs(rgba[probe + 2] - 128) <= 16;
            }

            if (!painted) Thread.Sleep(16);
        }

        Assert.True(texture != 0 && painted, "the page never reached the GPU");
        var (tw, th) = backend.TextureSize;

        var paint = new PaintList();
        paint.AddImage(bounds: new Rect(0, 0, 300, 200), pixelWidth: (int)tw, pixelHeight: (int)th,
            pixels: null, cacheKey: texture);
        engine.BeginFrame(1f / 60f);
        engine.SubmitPaintCommands(paint);

        string path = Path.Combine(Path.GetTempPath(), "zigote-webview-gpucheck.bmp");
        Assert.True(engine.CaptureUiBmp(path, width: 300, height: 200), "the capture failed");

        var (left, right) = (SampleBmp(path, 75, 100), SampleBmp(path, 225, 100));
        Assert.True(Close(left, (32, 64, 128)),
            $"left half sampled {left}, expected (32,64,128) — channels are swapped or rows are shifted");
        Assert.True(Close(right, (128, 64, 32)),
            $"right half sampled {right}, expected (128,64,32)");
    }

    private static bool Close((int R, int G, int B) got, (int R, int G, int B) want) =>
        Math.Abs(got.R - want.R) <= 8 && Math.Abs(got.G - want.G) <= 8 && Math.Abs(got.B - want.B) <= 8;

    /// <summary>One pixel out of a 24-bit BMP: bottom-up rows, BGR order, rows padded to 4 bytes.</summary>
    private static (int R, int G, int B) SampleBmp(string path, int x, int y)
    {
        byte[] bmp = File.ReadAllBytes(path);
        int offset = BitConverter.ToInt32(bmp, 10);
        int width = BitConverter.ToInt32(bmp, 18);
        int height = Math.Abs(BitConverter.ToInt32(bmp, 22));
        int stride = ((width * 3) + 3) & ~3;
        int row = offset + ((height - 1 - y) * stride) + (x * 3);
        return (bmp[row + 2], bmp[row + 1], bmp[row]);
    }
}
