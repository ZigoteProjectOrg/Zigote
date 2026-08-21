using Android.App;
using Android.Content;
using Android.Hardware;
using Android.Runtime;

namespace Sensors;

/// <summary>
///     Android implementation — <see cref="SensorManager" /> with one listener per sensor kind.
///     Android already reports the units this plugin promises (m/s², rad/s, µT), so the readings
///     pass through untouched.
/// </summary>
internal static class SensorsDriver
{
    private static readonly Dictionary<SensorKind, Listener> Running = [];

    private static SensorManager? Manager
        => (SensorManager?)Application.Context.GetSystemService(Context.SensorService);

    private static SensorType TypeOf(SensorKind kind) => kind switch
    {
        SensorKind.Gyroscope => SensorType.Gyroscope,
        SensorKind.Magnetometer => SensorType.MagneticField,
        SensorKind.LinearAcceleration => SensorType.LinearAcceleration,
        _ => SensorType.Accelerometer
    };

    public static bool IsAvailable(SensorKind kind) => Manager?.GetDefaultSensor(TypeOf(kind)) is not null;

    public static void Start(SensorKind kind, TimeSpan? interval, Action<SensorSample> publish)
    {
        var manager = Manager;
        if (manager?.GetDefaultSensor(TypeOf(kind)) is not { } sensor) return;

        var listener = new Listener(kind, publish);
        // The period is microseconds and a hint: the OS delivers at least this often, often more.
        int microseconds = interval is { } wanted
            ? (int)Math.Clamp(wanted.TotalMicroseconds, 1_000, 1_000_000)
            : (int)SensorDelay.Game;
        // The binding types the period as SensorDelay, but the Java parameter is a plain
        // microsecond int — the named constants are just three well-known values of it.
        manager.RegisterListener(listener, sensor, (SensorDelay)microseconds);
        Running[kind] = listener;
    }

    public static void Stop(SensorKind kind)
    {
        if (!Running.Remove(kind, out var listener)) return;
        Manager?.UnregisterListener(listener);
        listener.Dispose();
    }

    private sealed class Listener(SensorKind kind, Action<SensorSample> publish)
        : Java.Lang.Object, ISensorEventListener
    {
        public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy)
        {
        }

        public void OnSensorChanged(SensorEvent? e)
        {
            if (e?.Values is not { Count: >= 3 } values) return;
            // SensorEvent.timestamp is nanoseconds on the device's monotonic clock.
            publish(new SensorSample(
                kind, values[0], values[1], values[2], TimeSpan.FromTicks(e.Timestamp / 100)));
        }
    }
}
