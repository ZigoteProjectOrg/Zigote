using CoreLocation;
using Foundation;
using UIKit;

namespace Geolocation;

/// <summary>
///     iOS implementation — one <see cref="CLLocationManager" />, created on the main thread
///     (CoreLocation needs a run loop) and kept for the life of the app. <c>RequestLocation</c>
///     is the one-shot; <c>StartUpdatingLocation</c> is the stream.
///     <para>
///         Authorization is asked for on first use — the app still needs
///         <c>NSLocationWhenInUseUsageDescription</c> in Info.plist, or iOS refuses silently.
///     </para>
/// </summary>
internal static class GeolocationDriver
{
    private static CLLocationManager? _manager;
    private static Action<Position>? _publish;

    public static bool Available => CLLocationManager.LocationServicesEnabled;

    private static CLLocationManager Manager()
    {
        if (_manager is not null) return _manager;
        _manager = new CLLocationManager();
        _manager.LocationsUpdated += (_, e) =>
        {
            if (e.Locations.LastOrDefault() is { } location) Deliver(From(location));
        };
        _manager.Failed += (_, _) => Deliver(null);
        _manager.RequestWhenInUseAuthorization();
        return _manager;
    }

    /// <summary>Both consumers of a fix: the pending one-shot and the running stream.</summary>
    private static TaskCompletionSource<Position?>? _pending;

    private static void Deliver(Position? fix)
    {
        Interlocked.Exchange(ref _pending, null)?.TrySetResult(fix);
        if (fix is { } position) _publish?.Invoke(position);
    }

    public static async Task<Position?> GetAsync(GeoAccuracy accuracy, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Position?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var manager = Manager();
            manager.DesiredAccuracy = DesiredAccuracy(accuracy);
            manager.RequestLocation();
        });

        await using (cancellationToken.Register(() => tcs.TrySetResult(null)))
            return await tcs.Task;
    }

    public static void StartUpdates(GeoAccuracy accuracy, Action<Position> publish)
    {
        _publish = publish;
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var manager = Manager();
            manager.DesiredAccuracy = DesiredAccuracy(accuracy);
            manager.StartUpdatingLocation();
        });
    }

    public static void StopUpdates()
    {
        _publish = null;
        UIApplication.SharedApplication.InvokeOnMainThread(() => _manager?.StopUpdatingLocation());
    }

    private static double DesiredAccuracy(GeoAccuracy accuracy) => accuracy switch
    {
        GeoAccuracy.Coarse => CLLocation.AccuracyKilometer,
        GeoAccuracy.Fine => CLLocation.AccuracyBest,
        _ => CLLocation.AccuracyHundredMeters
    };

    /// <summary>CoreLocation reports "unknown" as a negative accuracy, speed or course.</summary>
    private static Position From(CLLocation location) => new(
        location.Coordinate.Latitude,
        location.Coordinate.Longitude,
        location.HorizontalAccuracy,
        location.VerticalAccuracy >= 0 ? location.Altitude : null,
        location.Speed >= 0 ? location.Speed : null,
        location.Course >= 0 ? location.Course : null,
        new DateTimeOffset((DateTime)location.Timestamp, TimeSpan.Zero));
}
