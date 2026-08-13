using Xunit;
using Zigote.Cinematics;
using Zigote.Scripting;

namespace Zigote.Tests;

/// <summary>
///     The <see cref="Camera" /> scripting provider forwards to its injected backend and is a safe
///     no-op when none is set (outside play). Mirrors the Audio/Physics provider tests.
/// </summary>
public class CameraProviderTests
{
    [Fact]
    public void Forwards_EveryControl_ToBackend()
    {
        var fake = new FakeCameraBackend();
        Camera.Backend = fake;
        try
        {
            Assert.True(Camera.IsAvailable);
            Camera.SetPhysicalEnabled(true);
            Camera.SetFocalLength(85f);
            Camera.SetSensor(SensorPreset.Super35);
            Camera.SetSensorSize(36f, 24f);
            Camera.SetAperture(1.4f);
            Camera.SetIso(400f);
            Camera.SetShutter(0.01f);
            Camera.SetFocusMode(FocusModeKind.Subject);
            Camera.SetManualFocus(3.5f);
            Camera.SetFilmStock(FilmStockKind.Kodak2383, 0.5f);

            Assert.True(fake.Enabled);
            Assert.Equal(85f, fake.Focal);
            Assert.Equal(SensorPreset.Super35, fake.Sensor);
            Assert.Equal(24f, fake.SensorH);
            Assert.Equal(1.4f, fake.FStop);
            Assert.Equal(400f, fake.Iso);
            Assert.Equal(0.01f, fake.Shutter);
            Assert.Equal(FocusModeKind.Subject, fake.Mode);
            Assert.Equal(3.5f, fake.ManualFocus);
            Assert.Equal(FilmStockKind.Kodak2383, fake.Stock);
            Assert.Equal(0.5f, fake.FilmStrength);
            Assert.Equal(10, fake.Calls);
        }
        finally
        {
            Camera.Backend = null;
        }
    }

    [Fact]
    public void NoOp_WhenBackendNull()
    {
        Camera.Backend = null;
        Assert.False(Camera.IsAvailable);
        // None of these should throw with no backend set.
        Camera.SetPhysicalEnabled(true);
        Camera.SetFocalLength(50f);
        Camera.SetFilmStock(FilmStockKind.Bw, 1f);
    }

    private sealed class FakeCameraBackend : ICameraBackend
    {
        public int Calls;
        public bool Enabled;
        public float Focal, FStop, Iso, Shutter, ManualFocus, FilmStrength, SensorW, SensorH;
        public FocusModeKind Mode;
        public SensorPreset Sensor;
        public FilmStockKind Stock;

        public void SetPhysicalEnabled(bool enabled)
        {
            Enabled = enabled;
            Calls++;
        }

        public void SetFocalLength(float mm)
        {
            Focal = mm;
            Calls++;
        }

        public void SetSensor(SensorPreset preset)
        {
            Sensor = preset;
            Calls++;
        }

        public void SetSensorSize(float w, float h)
        {
            SensorW = w;
            SensorH = h;
            Calls++;
        }

        public void SetAperture(float f)
        {
            FStop = f;
            Calls++;
        }

        public void SetIso(float i)
        {
            Iso = i;
            Calls++;
        }

        public void SetShutter(float s)
        {
            Shutter = s;
            Calls++;
        }

        public void SetFocusMode(FocusModeKind m)
        {
            Mode = m;
            Calls++;
        }

        public void SetManualFocus(float m)
        {
            ManualFocus = m;
            Calls++;
        }

        public void SetFilmStock(FilmStockKind st, float str)
        {
            Stock = st;
            FilmStrength = str;
            Calls++;
        }
    }
}
