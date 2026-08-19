using Camera;
using Xunit;
using Zigote.Core;

namespace Camera.Tests;

/// <summary>
///     The plugin's CPU-side logic, no camera and no engine: the latest-frame mailbox contract,
///     the .cube parser and its strip layout, the ffmpeg argument builders and device-list
///     parsers, and the view's fit arithmetic.
/// </summary>
public class CameraTests
{
    // ── FrameMailbox ────────────────────────────────────────────────────────────

    [Fact]
    public void Mailbox_LatestWins_AndRecyclesOverwrittenFrames()
    {
        var box = new FrameMailbox();

        byte[] first = box.Rent(16);
        box.Publish(buffer: first, width: 2, height: 2);
        byte[] second = box.Rent(16);
        Assert.NotSame(expected: first, actual: second); // first is still the unread latest
        box.Publish(buffer: second, width: 2, height: 2);

        Assert.True(box.TryTake(buffer: out byte[] taken, width: out int w, height: out int h));
        Assert.Same(expected: second, actual: taken); // newest frame, not the queue-oldest
        Assert.Equal(expected: 2, actual: w);
        Assert.Equal(expected: 2, actual: h);
        Assert.False(box.TryTake(buffer: out _, width: out _, height: out _)); // consumed

        // The overwritten first frame went back to the pool.
        Assert.Same(expected: first, actual: box.Rent(16));

        box.Return(taken);
        Assert.Same(expected: taken, actual: box.Rent(16));
    }

    [Fact]
    public void Mailbox_Rent_NeverHandsBackATooSmallBuffer()
    {
        var box = new FrameMailbox();
        box.Return(new byte[8]);
        Assert.True(box.Rent(32).Length >= 32);
    }

    // ── CameraLut (.cube) ───────────────────────────────────────────────────────

    [Fact]
    public void Cube_Parses_SizeDomainAndStripLayout()
    {
        // A 2-point identity cube, red-fastest order, with a comment and a title in the way.
        string[] cube =
        [
            "# a comment",
            "TITLE \"identity\"",
            "LUT_3D_SIZE 2",
            "0 0 0", // r=0 g=0 b=0
            "1 0 0", // r=1 g=0 b=0
            "0 1 0", // r=0 g=1 b=0
            "1 1 0",
            "0 0 1", // r=0 g=0 b=1  → second slice
            "1 0 1",
            "0 1 1",
            "1 1 1",
        ];
        var lut = CameraLut.Parse(cube);
        Assert.Equal(expected: 2, actual: lut.Size);

        byte[] strip = lut.StripForTesting;
        // Strip is (N*N)×N: texel x = b*N + r, y = g. Check r=1,g=0,b=1 → x=3, y=0 → (1,0,1).
        int at = ((0 * 4) + 3) * 4;
        Assert.Equal(expected: 255, actual: strip[at + 0]);
        Assert.Equal(expected: 0, actual: strip[at + 1]);
        Assert.Equal(expected: 255, actual: strip[at + 2]);
        Assert.Equal(expected: 255, actual: strip[at + 3]);
    }

    [Fact]
    public void Cube_DomainScaling_NormalizesTo255()
    {
        string[] cube =
        [
            "LUT_3D_SIZE 2",
            "DOMAIN_MIN 0.0 0.0 0.0",
            "DOMAIN_MAX 2.0 2.0 2.0",
            "0 0 0", "2 0 0", "0 2 0", "2 2 0", "0 0 2", "2 0 2", "0 2 2", "2 2 2",
        ];
        byte[] strip = CameraLut.Parse(cube).StripForTesting;
        Assert.Equal(expected: 255, actual: strip[((0 * 4) + 1) * 4]); // r=1 slice0 → red 2.0/2.0
    }

    [Fact]
    public void Cube_Incomplete_Throws()
    {
        Assert.Throws<InvalidDataException>(() => CameraLut.Parse(["LUT_3D_SIZE 2", "0 0 0"]));
        Assert.Throws<InvalidDataException>(() => CameraLut.Parse(["0 0 0"]));
    }

    // ── Desktop ffmpeg seam ─────────────────────────────────────────────────────

    [Fact]
    public void CaptureArgs_PinExactOutputGeometry()
    {
        string[] args = CameraDriver.CaptureArgs(deviceId: "/dev/video0", width: 1280, height: 720).ToArray();
        Assert.Contains(expected: "scale=1280:720", collection: args);
        Assert.Contains(expected: "rawvideo", collection: args);
        Assert.Contains(expected: "rgba", collection: args);
        Assert.Equal(expected: "pipe:1", actual: args[^1]);
    }

    [Fact]
    public void ProbeArgs_EndWithTheDevice_AndDeclareTheCaptureFormat()
    {
        string[] args = CameraDriver.ProbeArgs("/dev/video0").ToArray();
        Assert.Equal(expected: "/dev/video0", actual: args[^1]);
        Assert.Equal(expected: "-i", actual: args[^2]);
        Assert.Contains(expected: "-f", collection: args);
        Assert.Contains(expected: "-show_streams", collection: args);
    }

    [Fact]
    public void JpegArgs_MapQualityOntoMjpegScale()
    {
        string[] best = CameraDriver.JpegArgs(width: 4, height: 4, quality: 100).ToArray();
        string[] worst = CameraDriver.JpegArgs(width: 4, height: 4, quality: 1).ToArray();
        Assert.Contains(expected: "2", collection: best); // -q:v 2 is mjpeg's best
        Assert.Contains(expected: "31", collection: worst);
        Assert.Contains(expected: "4x4", collection: best);
    }

    [Fact]
    public void OutputSize_CapsHeight_KeepsAspect_EvenWidth_NeverUpscales()
    {
        Assert.Equal(expected: (1280, 720), actual: CameraDriver.OutputSize(nativeWidth: 1920, nativeHeight: 1080, maxHeight: 720));
        Assert.Equal(expected: (640, 480), actual: CameraDriver.OutputSize(nativeWidth: 640, nativeHeight: 480, maxHeight: 720));
        Assert.Equal(expected: (1920, 1080), actual: CameraDriver.OutputSize(nativeWidth: 1920, nativeHeight: 1080, maxHeight: 0));
        // 1000/750 scaled to 500 → width 666.67 rounds to the even 666.
        Assert.Equal(expected: (666, 500), actual: CameraDriver.OutputSize(nativeWidth: 1000, nativeHeight: 750, maxHeight: 500));
    }

    [Fact]
    public void AvFoundationDevices_ParseVideoSection_SkippingScreens()
    {
        const string stderr =
            """
            [AVFoundation indev @ 0x7f8] AVFoundation video devices:
            [AVFoundation indev @ 0x7f8] [0] FaceTime HD Camera
            [AVFoundation indev @ 0x7f8] [1] Capture screen 0
            [AVFoundation indev @ 0x7f8] AVFoundation audio devices:
            [AVFoundation indev @ 0x7f8] [0] MacBook Pro Microphone
            """;
        var devices = CameraDriver.ParseAvFoundationDevices(stderr);
        var device = Assert.Single(devices);
        Assert.Equal(expected: "0", actual: device.Id);
        Assert.Equal(expected: "FaceTime HD Camera", actual: device.Name);
    }

    [Fact]
    public void DshowDevices_ParseQuotedVideoNames()
    {
        const string stderr =
            """
            [dshow @ 000001] "HD WebCam" (video)
            [dshow @ 000001]   Alternative name "@device_pnp_\\?\usb#vid"
            [dshow @ 000001] "Microphone Array" (audio)
            """;
        var devices = CameraDriver.ParseDshowDevices(stderr);
        var device = Assert.Single(devices);
        Assert.Equal(expected: "HD WebCam", actual: device.Id);
    }

    // ── CameraView fit ──────────────────────────────────────────────────────────

    [Fact]
    public void FitRect_ContainLetterboxes_CoverOverflows()
    {
        var box = new Rect(x: 0, y: 0, width: 100, height: 100);

        var contain = CameraView.FitRect(box: box, aspect: 2f, fit: CameraFit.Contain);
        Assert.Equal(expected: 100, actual: contain.Width);
        Assert.Equal(expected: 50, actual: contain.Height);
        Assert.Equal(expected: 25, actual: contain.Y);

        var cover = CameraView.FitRect(box: box, aspect: 2f, fit: CameraFit.Cover);
        Assert.Equal(expected: 200, actual: cover.Width);
        Assert.Equal(expected: 100, actual: cover.Height);
        Assert.Equal(expected: -50, actual: cover.X);

        Assert.Equal(expected: box, actual: CameraView.FitRect(box: box, aspect: 2f, fit: CameraFit.Fill));
    }
}
