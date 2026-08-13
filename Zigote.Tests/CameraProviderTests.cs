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
            Camera.SetSensorSize(widthMm: 36f, heightMm: 24f);
            Camera.SetAperture(1.4f);
            Camera.SetIso(400f);
            Camera.SetShutter(0.01f);
            Camera.SetFocusMode(FocusModeKind.Subject);
            Camera.SetManualFocus(3.5f);
            Camera.SetFilmStock(stock: FilmStockKind.Kodak2383, strength: 0.5f);

            Assert.True(fake.Enabled);
            Assert.Equal(expected: 85f, actual: fake.Focal);
            Assert.Equal(expected: SensorPreset.Super35, actual: fake.Sensor);
            Assert.Equal(expected: 24f, actual: fake.SensorH);
            Assert.Equal(expected: 1.4f, actual: fake.FStop);
            Assert.Equal(expected: 400f, actual: fake.Iso);
            Assert.Equal(expected: 0.01f, actual: fake.Shutter);
            Assert.Equal(expected: FocusModeKind.Subject, actual: fake.Mode);
            Assert.Equal(expected: 3.5f, actual: fake.ManualFocus);
            Assert.Equal(expected: FilmStockKind.Kodak2383, actual: fake.Stock);
            Assert.Equal(expected: 0.5f, actual: fake.FilmStrength);
            Assert.Equal(expected: 10, actual: fake.Calls);
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
        Camera.SetFilmStock(stock: FilmStockKind.Bw, strength: 1f);
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
