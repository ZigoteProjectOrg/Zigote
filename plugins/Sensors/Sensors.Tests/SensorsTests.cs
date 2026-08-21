using Xunit;

namespace Sensors.Tests;

/// <summary>
///     The shared bookkeeping — per-kind routing, and that a dispose really unsubscribes. The
///     sensors themselves belong to the OS; desktop has none, which is checked too.
/// </summary>
public class SensorsTests
{
    private static SensorSample Sample(SensorKind kind, double x)
        => new(kind, x, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Listen_RoutesByKind_AndStopsOnDispose()
    {
        List<SensorSample> accelerometer = [];
        List<SensorSample> gyroscope = [];
        var first = SensorsPlugin.Listen(SensorKind.Accelerometer, accelerometer.Add);
        var second = SensorsPlugin.Listen(SensorKind.Gyroscope, gyroscope.Add);

        SensorsPlugin.Publish(Sample(SensorKind.Accelerometer, 1));
        SensorsPlugin.Publish(Sample(SensorKind.Gyroscope, 2));
        SensorsPlugin.Publish(Sample(SensorKind.Magnetometer, 3));  // nobody listening
        first.Dispose();
        SensorsPlugin.Publish(Sample(SensorKind.Accelerometer, 4)); // gone — not heard
        second.Dispose();

        Assert.Equal([Sample(SensorKind.Accelerometer, 1)], accelerometer);
        Assert.Equal([Sample(SensorKind.Gyroscope, 2)], gyroscope);
    }

    [Fact]
    public void Magnitude_IsTheVectorLength()
        => Assert.Equal(5, new SensorSample(SensorKind.Accelerometer, 3, 4, 0, TimeSpan.Zero).Magnitude);

    [Fact]
    public void Desktop_HasNoSensors()
        => Assert.All(Enum.GetValues<SensorKind>(), kind => Assert.False(SensorsPlugin.IsAvailable(kind)));
}
