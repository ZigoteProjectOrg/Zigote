namespace Sensors;

/// <summary>The four streams every phone exposes and every app asks for.</summary>
public enum SensorKind
{
    /// <summary>Total acceleration including gravity, m/s². A phone at rest reads ~9.8 on one axis.</summary>
    Accelerometer,

    /// <summary>Rotation rate, rad/s.</summary>
    Gyroscope,

    /// <summary>Magnetic field, microtesla — a compass, once you correct for tilt.</summary>
    Magnetometer,

    /// <summary>Acceleration with gravity taken out, m/s² — what the user did to the device.</summary>
    LinearAcceleration
}

/// <summary>
///     One reading, in the sensor's own axes: x right, y up, z out of the screen, with the
///     device held upright.
/// </summary>
/// <param name="Kind">Which sensor produced it.</param>
/// <param name="X">First axis, in the unit documented on <see cref="SensorKind" />.</param>
/// <param name="Y">Second axis.</param>
/// <param name="Z">Third axis.</param>
/// <param name="Timestamp">When the reading was taken, as the platform's own monotonic clock reports it.</param>
public readonly record struct SensorSample(
    SensorKind Kind, double X, double Y, double Z, TimeSpan Timestamp)
{
    /// <summary>Magnitude of the vector — total acceleration, total rotation rate, field strength.</summary>
    public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>
///     Sensors — accelerometer, gyroscope, magnetometer and linear acceleration as streams. The
///     <c>sensors_plus</c> slot from the plugin roadmap. Static, nothing to register with
///     <c>PluginHost</c>: the platform sensor is switched on with the first listener for that
///     kind and off with the last, because a sensor left running is a battery bill.
///     <para>
///         Samples arrive on the OS's sensor thread, often at 50–200 Hz — post to the app thread
///         before touching widgets, and do the maths in the handler, not the UI.
///     </para>
/// </summary>
public static class SensorsPlugin
{
    /// <summary>Standard gravity, for the platforms that report acceleration in g.</summary>
    internal const double StandardGravity = 9.80665;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<SensorKind, List<Action<SensorSample>>> Listeners = [];

    /// <summary>Whether this device has that sensor. False for everything on desktop.</summary>
    public static bool IsAvailable(SensorKind kind) => SensorsDriver.IsAvailable(kind);

    /// <summary>
    ///     Follow a sensor. Dispose to stop. Subscribing to a sensor the device does not have is
    ///     allowed and simply never fires — an app that works better with a gyroscope should not
    ///     need a different code path on a device without one.
    /// </summary>
    /// <param name="interval">
    ///     Requested time between samples. The OS treats it as a hint and usually delivers
    ///     faster; null asks for the platform default (~50 Hz).
    /// </param>
    public static IDisposable Listen(
        SensorKind kind, Action<SensorSample> onSample, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        lock (Gate)
        {
            if (!Listeners.TryGetValue(kind, out var handlers))
                Listeners[kind] = handlers = [];
            handlers.Add(onSample);
            if (handlers.Count == 1 && IsAvailable(kind)) SensorsDriver.Start(kind, interval, Publish);
        }

        return new Subscription(kind, onSample);
    }

    /// <summary>What the platform sensor calls with each reading.</summary>
    internal static void Publish(SensorSample sample)
    {
        Action<SensorSample>[] handlers;
        lock (Gate)
        {
            if (!Listeners.TryGetValue(sample.Kind, out var list)) return;
            handlers = list.ToArray();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(sample);
            }
            catch (Exception)
            {
                // A throwing handler must not take the sensor down for everyone else.
            }
        }
    }

    private static void Remove(SensorKind kind, Action<SensorSample> handler)
    {
        lock (Gate)
        {
            if (!Listeners.TryGetValue(kind, out var handlers)) return;
            if (!handlers.Remove(handler) || handlers.Count > 0) return;
            Listeners.Remove(kind);
            if (IsAvailable(kind)) SensorsDriver.Stop(kind);
        }
    }

    private sealed class Subscription(SensorKind kind, Action<SensorSample> handler) : IDisposable
    {
        private Action<SensorSample>? _handler = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _handler, null) is { } h) Remove(kind, h);
        }
    }
}
