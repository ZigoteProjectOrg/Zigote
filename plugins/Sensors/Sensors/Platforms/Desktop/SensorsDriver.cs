namespace Sensors;

/// <summary>
///     Desktop implementation — laptops do have accelerometers (Linux exposes some through the
///     IIO subsystem, and Windows has its own sensor API), but nothing an app can rely on across
///     machines, and a screen-rotation sensor is not the motion stream this plugin promises.
///     <para>
///         ponytail: every sensor reports unavailable and never fires. Add IIO on Linux when a
///         desktop app needs tilt.
///     </para>
/// </summary>
internal static class SensorsDriver
{
    public static bool IsAvailable(SensorKind kind) => false;

    public static void Start(SensorKind kind, TimeSpan? interval, Action<SensorSample> publish)
    {
    }

    public static void Stop(SensorKind kind)
    {
    }
}
