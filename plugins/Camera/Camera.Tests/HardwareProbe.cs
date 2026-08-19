using Camera;
using Xunit;

namespace Camera.Tests;

/// <summary>
///     Ad-hoc hardware diagnostic, not part of the suite: opens the real default camera through
///     the desktop driver and reports what arrives. Run explicitly with
///     --filter DriverDeliversRealFrames.
/// </summary>
public class HardwareProbe
{
    /// <summary>Set ZIGOTE_CAMERA_HW=1 to run against the machine's real camera.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("ZIGOTE_CAMERA_HW") == "1";

    [Fact(Skip = "hardware diagnostic — unskip to run against a real camera",
        SkipUnless = nameof(Enabled), SkipType = typeof(HardwareProbe))]
    public async Task DriverDeliversRealFrames()
    {
        var frames = new FrameMailbox();
        string? error = null;
        using var session = CameraDriver.Open(
            deviceId: null,
            maxHeight: 720,
            minimalProcessing: true,
            frames: frames,
            onError: e => error = e
        );

        byte[]? frame = null;
        int w = 0, h = 0, seen = 0;
        for (int i = 0; i < 100 && error is null; i++)
        {
            await Task.Delay(100);
            if (frames.TryTake(buffer: out byte[] taken, width: out w, height: out h))
            {
                seen++;
                if (frame is not null) frames.Return(frame);
                frame = taken;
                if (seen >= 20) break; // well past auto-exposure warm-up
            }
        }

        Assert.Null(error);
        Assert.NotNull(frame);
        double r = 0, g = 0, b = 0;
        int n = w * h;
        for (int i = 0; i < n * 4; i += 4)
        {
            r += frame[i];
            g += frame[i + 1];
            b += frame[i + 2];
        }

        // Print the evidence whatever happens; fail only if the image is essentially black.
        double avg = (r + g + b) / (3.0 * n);
        Assert.True(avg > 8, $"frames={seen} size={w}x{h} but avg brightness {avg:F1} — black frames");
        Assert.True(seen >= 5, $"only {seen} frames in 10s");
    }
}
