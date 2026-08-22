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

    // ── Manual controls ─────────────────────────────────────────────────────────

    [Fact]
    public void ExposureMode_IsDerivedFromWhichDialsAreHeld()
    {
        var controls = new CameraControls();
        Assert.Equal(expected: ExposureMode.Auto, actual: controls.Mode);

        controls.Iso.Value = 400;
        Assert.Equal(expected: ExposureMode.IsoPriority, actual: controls.Mode);

        controls.ShutterNs.Value = 4_000_000; // 1/250
        Assert.Equal(expected: ExposureMode.Manual, actual: controls.Mode);

        controls.Iso.Value = 0; // handed back to the device
        Assert.Equal(expected: ExposureMode.ShutterPriority, actual: controls.Mode);

        controls.ResetToAuto();
        Assert.Equal(expected: ExposureMode.Auto, actual: controls.Mode);
        Assert.True(float.IsNaN(controls.FocusDiopters.Value));
    }

    [Fact]
    public void ClampTo_PullsDialsIntoRange_AndDropsWhatTheLensCannotDo()
    {
        var caps = new CameraCapabilities(
            Iso: new IsoRange(Min: 100, Max: 3200),
            Shutter: new ShutterRange(MinNs: 100_000, MaxNs: 500_000_000),
            EvStep: 1f / 3f,
            EvRange: (-9, 9),
            Kelvin: (0, 0),      // this lens has no manual white balance
            Tint: false,
            MinFocusDiopters: 0f,
            ManualFocus: false,  // …and no manual focus
            OisToggle: false,
            Regions: true,
            Raw: false
        );

        var controls = new CameraControls();
        controls.Iso.Value = 12800;              // past this sensor's ceiling
        controls.ShutterNs.Value = 1_000_000_000; // 1s, longer than it allows
        controls.WhiteBalanceKelvin.Value = 5200;
        controls.FocusDiopters.Value = 2f;
        controls.EvCompensation.Value = 5f;      // past ±3 stops
        controls.Ois.Value = false;

        controls.ClampTo(caps);

        Assert.Equal(expected: 3200, actual: controls.Iso.Value);
        Assert.Equal(expected: 500_000_000, actual: controls.ShutterNs.Value);
        // Unsupported controls go back to auto rather than keeping a value nothing will honour.
        Assert.Equal(expected: 0, actual: controls.WhiteBalanceKelvin.Value);
        Assert.True(float.IsNaN(controls.FocusDiopters.Value));
        Assert.Equal(expected: 3f, actual: controls.EvCompensation.Value, tolerance: 0.001f);
        Assert.True(controls.Ois.Value); // no toggle means it is on, whatever was asked
    }

    [Fact]
    public void ClampTo_None_LeavesEverythingOnAuto()
    {
        var controls = new CameraControls();
        controls.Iso.Value = 800;
        controls.ShutterNs.Value = 4_000_000;

        controls.ClampTo(CameraCapabilities.None);

        Assert.Equal(expected: ExposureMode.Auto, actual: controls.Mode);
        Assert.False(CameraCapabilities.None.AnyManual);
    }

    [Fact]
    public void Snapshot_ReadsEveryControlAtOnce()
    {
        var controls = new CameraControls();
        controls.Iso.Value = 200;
        controls.ShutterNs.Value = 8_000_000;
        controls.AeRegion.Value = new Rect(x: 0.25f, y: 0.25f, width: 0.5f, height: 0.5f);
        controls.AwbLock.Value = true;

        ControlState snapshot = controls.Snapshot();

        Assert.Equal(expected: 200, actual: snapshot.Iso);
        Assert.Equal(expected: ExposureMode.Manual, actual: snapshot.Mode);
        Assert.True(snapshot.AwbLock);
        Assert.Equal(expected: 0.25f, actual: snapshot.AeRegion!.Value.X);

        // A snapshot is a value: later dial movement cannot reach back into it.
        controls.Iso.Value = 6400;
        Assert.Equal(expected: 200, actual: snapshot.Iso);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(3200)]
    [InlineData(5200)]
    [InlineData(6500)]
    [InlineData(9000)]
    public void WhiteBalanceGains_AreAlwaysLegalForCamera2(int kelvin)
    {
        (float r, float g, float b) = WhiteBalance.GainsFor(kelvin);

        // COLOR_CORRECTION_GAINS rejects anything below 1.0 on any channel.
        Assert.True(r >= 1f, $"red gain {r} < 1 at {kelvin}K");
        Assert.True(g >= 1f, $"green gain {g} < 1 at {kelvin}K");
        Assert.True(b >= 1f, $"blue gain {b} < 1 at {kelvin}K");
    }

    [Fact]
    public void WhiteBalanceGains_CorrectTheLight_RatherThanMatchingIt()
    {
        // Warm light is red-heavy, so neutralizing it means boosting blue — and the other way
        // for cool light. Getting this backwards is the classic white-balance sign error, and it
        // looks plausible on screen until you compare two temperatures.
        (float warmR, _, float warmB) = WhiteBalance.GainsFor(3000);
        Assert.True(warmB > warmR, "3000 K should lift blue to cancel warm light");

        (float coolR, _, float coolB) = WhiteBalance.GainsFor(9000);
        Assert.True(coolR > coolB, "9000 K should lift red to cancel cool light");

        // And it has to be monotonic, or a dial sweep would wobble instead of warming.
        float previous = 0f;
        for (int k = WhiteBalance.MinKelvin; k <= WhiteBalance.MaxKelvin; k += 500)
        {
            (float r, float g, float b) = WhiteBalance.GainsFor(k);
            float ratio = r / b;
            Assert.True(ratio >= previous, $"red/blue ratio dipped at {k}K");
            previous = ratio;

            // Normalization scales the whole set so the darkest channel sits exactly at 1.0 —
            // that is an exposure change, not a hue change, and it is what keeps every gain
            // inside what COLOR_CORRECTION_GAINS accepts.
            Assert.Equal(
                expected: 1f,
                actual: Math.Min(val1: Math.Min(val1: r, val2: g), val2: b),
                tolerance: 0.001f
            );
        }
    }

    [Fact]
    public void WhiteBalanceTint_TradesGreenAgainstMagenta()
    {
        (float _, float neutralG, float _) = WhiteBalance.GainsFor(kelvin: 5200, tint: 0f);
        (float _, float greenG, float _) = WhiteBalance.GainsFor(kelvin: 5200, tint: -1f);
        (float _, float magentaG, float _) = WhiteBalance.GainsFor(kelvin: 5200, tint: 1f);

        // More green gain means a greener picture; magenta is the other end of the same axis.
        Assert.True(greenG > neutralG);
        Assert.True(magentaG < greenG);
    }

    [Fact]
    public void ShutterLabel_ReadsLikeACameraBody()
    {
        Assert.Equal(expected: "1/250", actual: Meta(4_000_000).ShutterLabel);
        Assert.Equal(expected: "1/60", actual: Meta(16_666_667).ShutterLabel);
        Assert.Equal(expected: "2\"", actual: Meta(2_000_000_000).ShutterLabel);
        Assert.Equal(expected: "—", actual: Meta(0).ShutterLabel);

        static CaptureMetadata Meta(long ns) => new(
            Iso: 100,
            ShutterNs: ns,
            Aperture: 1.8f,
            FocalLengthMm: 5.6f,
            FocusDiopters: float.NaN,
            Kelvin: 0,
            AeConverged: true,
            AfConverged: true
        );
    }

    [Fact]
    public void EvStops_TranslateStepsIntoWhatADialShows()
    {
        var caps = CameraCapabilities.None with { EvStep = 1f / 3f, EvRange = (-9, 9) };
        (float min, float max) = caps.EvStops;
        Assert.Equal(expected: -3f, actual: min, tolerance: 0.001f);
        Assert.Equal(expected: 3f, actual: max, tolerance: 0.001f);

        // No step means no EV control at all, not a dial that runs 0 to 0.
        Assert.Equal(expected: (0f, 0f), actual: CameraCapabilities.None.EvStops);
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
    public void SyntheticDevice_GoesToLavfi_NotTheCaptureFormat()
    {
        string[] args = CameraDriver
            .CaptureArgs(deviceId: "lavfi:testsrc2=size=640x480:rate=30", width: 640, height: 480)
            .ToArray();

        // The prefix is stripped and the filtergraph becomes the input: a synthetic camera must
        // never reach v4l2/avfoundation/dshow, which would try to open it as a device node.
        Assert.Contains(expected: "lavfi", collection: args);
        Assert.DoesNotContain(expected: "v4l2", collection: args);
        Assert.Equal(expected: "testsrc2=size=640x480:rate=30", actual: args[Array.IndexOf(args, "-i") + 1]);

        // Same substitution on the probe, or the geometry lookup would open a device that is
        // not there and the session would fail before the first frame.
        string[] probe = CameraDriver.ProbeArgs("lavfi:testsrc2=size=640x480:rate=30").ToArray();
        Assert.Equal(expected: "testsrc2=size=640x480:rate=30", actual: probe[^1]);
        Assert.Contains(expected: "lavfi", collection: probe);
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
