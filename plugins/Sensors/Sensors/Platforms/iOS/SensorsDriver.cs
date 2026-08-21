using CoreMotion;
using Foundation;

namespace Sensors;

/// <summary>
///     iOS implementation — one <see cref="CMMotionManager" /> for everything. CoreMotion reports
///     acceleration in g and rotation in rad/s, so acceleration is scaled to m/s² to match what
///     Android reports and what <see cref="SensorKind" /> documents. Linear acceleration comes
///     from device motion, which is where iOS keeps the gravity-free vector.
/// </summary>
internal static class SensorsDriver
{
    private static readonly CMMotionManager Motion = new();
    private static readonly NSOperationQueue Queue = new();

    public static bool IsAvailable(SensorKind kind) => kind switch
    {
        SensorKind.Gyroscope => Motion.GyroAvailable,
        SensorKind.Magnetometer => Motion.MagnetometerAvailable,
        SensorKind.LinearAcceleration => Motion.DeviceMotionAvailable,
        _ => Motion.AccelerometerAvailable
    };

    public static void Start(SensorKind kind, TimeSpan? interval, Action<SensorSample> publish)
    {
        double seconds = interval?.TotalSeconds is > 0 and var wanted ? Math.Clamp(wanted, 0.001, 1) : 0.02;

        switch (kind)
        {
            case SensorKind.Gyroscope:
                Motion.GyroUpdateInterval = seconds;
                Motion.StartGyroUpdates(Queue, (data, _) =>
                {
                    if (data is not null)
                        publish(Sample(kind, data.RotationRate.x, data.RotationRate.y, data.RotationRate.z,
                            data.Timestamp, 1));
                });
                break;
            case SensorKind.Magnetometer:
                Motion.MagnetometerUpdateInterval = seconds;
                Motion.StartMagnetometerUpdates(Queue, (data, _) =>
                {
                    if (data is not null)
                        publish(Sample(kind, data.MagneticField.X, data.MagneticField.Y, data.MagneticField.Z,
                            data.Timestamp, 1));
                });
                break;
            case SensorKind.LinearAcceleration:
                Motion.DeviceMotionUpdateInterval = seconds;
                Motion.StartDeviceMotionUpdates(Queue, (data, _) =>
                {
                    if (data is not null)
                        publish(Sample(kind, data.UserAcceleration.X, data.UserAcceleration.Y,
                            data.UserAcceleration.Z, data.Timestamp, SensorsPlugin.StandardGravity));
                });
                break;
            default:
                Motion.AccelerometerUpdateInterval = seconds;
                Motion.StartAccelerometerUpdates(Queue, (data, _) =>
                {
                    if (data is not null)
                        publish(Sample(kind, data.Acceleration.X, data.Acceleration.Y, data.Acceleration.Z,
                            data.Timestamp, SensorsPlugin.StandardGravity));
                });
                break;
        }
    }

    public static void Stop(SensorKind kind)
    {
        switch (kind)
        {
            case SensorKind.Gyroscope: Motion.StopGyroUpdates(); break;
            case SensorKind.Magnetometer: Motion.StopMagnetometerUpdates(); break;
            case SensorKind.LinearAcceleration: Motion.StopDeviceMotionUpdates(); break;
            default: Motion.StopAccelerometerUpdates(); break;
        }
    }

    private static SensorSample Sample(
        SensorKind kind, double x, double y, double z, double timestamp, double scale)
        => new(kind, x * scale, y * scale, z * scale, TimeSpan.FromSeconds(timestamp));
}
