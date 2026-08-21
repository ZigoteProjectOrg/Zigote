namespace Geolocation;

/// <summary>How hard the OS should work for a fix — accuracy costs battery and time.</summary>
public enum GeoAccuracy
{
    /// <summary>Cell towers and Wi-Fi: city-block accuracy, cheap, fast.</summary>
    Coarse,

    /// <summary>Whatever the OS thinks is a fair trade — the default.</summary>
    Balanced,

    /// <summary>GPS: metres, at the cost of battery and a cold-start wait.</summary>
    Fine
}

/// <summary>
///     One position fix. Fields the platform did not report are null — a network fix has no
///     altitude, a stationary phone has no heading.
/// </summary>
/// <param name="Latitude">Degrees, -90..90.</param>
/// <param name="Longitude">Degrees, -180..180.</param>
/// <param name="AccuracyMeters">Radius of 68% confidence, as the OS reports it.</param>
/// <param name="AltitudeMeters">Metres above the WGS84 ellipsoid.</param>
/// <param name="SpeedMps">Ground speed in metres per second.</param>
/// <param name="HeadingDegrees">Direction of travel, 0 = north, clockwise.</param>
/// <param name="Timestamp">When the fix was taken, not when it was delivered.</param>
public readonly record struct Position(
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    double? AltitudeMeters,
    double? SpeedMps,
    double? HeadingDegrees,
    DateTimeOffset Timestamp);

/// <summary>
///     Geolocation — where the device is, once or as a stream. The <c>geolocator</c> slot from
///     the plugin roadmap. Static, nothing to register with <c>PluginHost</c>.
///     <para>
///         Location is a permission before it is an API: on Android and iOS, ask through the
///         Permissions plugin first. Without it every call answers null and no updates arrive —
///         there is no exception to catch, because "the user said no" is not an error.
///     </para>
/// </summary>
public static class GeolocationPlugin
{
    private static readonly Lock Gate = new();
    private static readonly List<Watcher> Watchers = [];

    /// <summary>False where the device has no location services at all — every desktop, for now.</summary>
    public static bool Available => GeolocationDriver.Available;

    /// <summary>
    ///     One fix. Null when location is unavailable, not permitted, or nothing arrived before
    ///     the token was cancelled — a phone indoors can take a while at <see cref="GeoAccuracy.Fine" />.
    /// </summary>
    public static Task<Position?> GetAsync(
        GeoAccuracy accuracy = GeoAccuracy.Balanced, CancellationToken cancellationToken = default)
        => Available
            ? GeolocationDriver.GetAsync(accuracy, cancellationToken)
            : Task.FromResult<Position?>(null);

    /// <summary>
    ///     Follow the device. Fixes arrive on the OS's thread — post before touching widgets.
    ///     Dispose to stop; the last dispose stops the platform's location updates too.
    /// </summary>
    /// <param name="minimumDistanceMeters">
    ///     Drop fixes closer than this to the last one delivered. Applied here rather than left
    ///     to the platform so every platform behaves the same way.
    /// </param>
    public static IDisposable Listen(
        Action<Position> onFix,
        GeoAccuracy accuracy = GeoAccuracy.Balanced,
        double minimumDistanceMeters = 0)
    {
        ArgumentNullException.ThrowIfNull(onFix);
        var watcher = new Watcher(onFix, minimumDistanceMeters);
        lock (Gate)
        {
            Watchers.Add(watcher);
            if (Watchers.Count == 1 && Available) GeolocationDriver.StartUpdates(accuracy, Publish);
        }

        return watcher;
    }

    /// <summary>What the platform calls with each fix.</summary>
    internal static void Publish(Position fix)
    {
        Watcher[] watchers;
        lock (Gate) watchers = Watchers.ToArray();
        foreach (var watcher in watchers) watcher.Deliver(fix);
    }

    private static void Remove(Watcher watcher)
    {
        lock (Gate)
        {
            if (!Watchers.Remove(watcher) || Watchers.Count > 0) return;
            if (Available) GeolocationDriver.StopUpdates();
        }
    }

    /// <summary>
    ///     Great-circle distance in metres. Haversine on a sphere: good to a few metres per
    ///     kilometre, which is well inside the accuracy of the fixes it filters.
    /// </summary>
    internal static double DistanceMeters(Position a, Position b)
    {
        const double earthRadius = 6_371_000;
        double lat1 = double.DegreesToRadians(a.Latitude), lat2 = double.DegreesToRadians(b.Latitude);
        double dLat = lat2 - lat1;
        double dLon = double.DegreesToRadians(b.Longitude - a.Longitude);
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthRadius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private sealed class Watcher(Action<Position> onFix, double minimumDistanceMeters) : IDisposable
    {
        private Action<Position>? _onFix = onFix;
        private Position? _lastDelivered;

        public void Deliver(Position fix)
        {
            var handler = _onFix;
            if (handler is null) return;
            if (_lastDelivered is { } last && minimumDistanceMeters > 0 &&
                DistanceMeters(last, fix) < minimumDistanceMeters)
                return;

            _lastDelivered = fix;
            try
            {
                handler(fix);
            }
            catch (Exception)
            {
                // One bad handler does not stop the others from being told where they are.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _onFix, null) is not null) Remove(this);
        }
    }
}
