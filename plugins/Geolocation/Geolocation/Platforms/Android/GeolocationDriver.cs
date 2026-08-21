using Android.App;
using Android.Content;
using Android.Locations;
using Android.OS;

namespace Geolocation;

/// <summary>
///     Android implementation — the framework <see cref="LocationManager" />, not Play Services:
///     the fused provider needs a Google dependency and this needs a provider name and a
///     listener. GPS for <see cref="GeoAccuracy.Fine" />, the network provider otherwise, with a
///     fall back to whatever is enabled.
///     <para>
///         Without <c>ACCESS_COARSE_LOCATION</c>/<c>ACCESS_FINE_LOCATION</c> granted, every call
///         here answers empty rather than throwing: a refused permission is an answer.
///     </para>
/// </summary>
internal static class GeolocationDriver
{
    private static Listener? _updates;

    private static LocationManager? Manager
        => (LocationManager?)Application.Context.GetSystemService(Context.LocationService);

    public static bool Available
    {
        get
        {
            try
            {
                return Manager?.GetProviders(true)?.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>The best enabled provider for the accuracy asked for, or null if none is enabled.</summary>
    private static string? Provider(GeoAccuracy accuracy)
    {
        var manager = Manager;
        if (manager is null) return null;
        IList<string> enabled = manager.GetProviders(true) ?? [];
        string preferred = accuracy == GeoAccuracy.Fine
            ? LocationManager.GpsProvider
            : LocationManager.NetworkProvider;
        if (enabled.Contains(preferred)) return preferred;
        return enabled.Count > 0 ? enabled[0] : null;
    }

    public static async Task<Position?> GetAsync(
        GeoAccuracy accuracy, CancellationToken cancellationToken)
    {
        var manager = Manager;
        if (manager is null || Provider(accuracy) is not { } provider) return null;

        var tcs = new TaskCompletionSource<Position?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Listener? listener = null;
        try
        {
            listener = new Listener(fix => tcs.TrySetResult(fix));
            manager.RequestLocationUpdates(provider, 0L, 0f, listener, Looper.MainLooper);

            // A last known fix answers instantly while the radio warms up; it is only used when
            // it is recent enough to still describe where the device is.
            if (manager.GetLastKnownLocation(provider) is { } known &&
                DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(known.Time) < TimeSpan.FromSeconds(30))
                tcs.TrySetResult(From(known));

            await using (cancellationToken.Register(() => tcs.TrySetResult(null)))
                return await tcs.Task;
        }
        catch (Exception)
        {
            // SecurityException when the permission is not held; IllegalArgumentException when
            // the provider disappears between the check and the call.
            return null;
        }
        finally
        {
            if (listener is not null) TryRemove(listener);
        }
    }

    public static void StartUpdates(GeoAccuracy accuracy, Action<Position> publish)
    {
        var manager = Manager;
        if (manager is null || Provider(accuracy) is not { } provider) return;

        try
        {
            _updates = new Listener(publish);
            // One second / no distance filter: the shared layer owns the distance filtering, and
            // a listener that never fires cannot be filtered later.
            manager.RequestLocationUpdates(provider, 1000L, 0f, _updates, Looper.MainLooper);
        }
        catch (Exception)
        {
            _updates = null;
        }
    }

    public static void StopUpdates()
    {
        if (_updates is null) return;
        TryRemove(_updates);
        _updates = null;
    }

    private static void TryRemove(Listener listener)
    {
        try
        {
            Manager?.RemoveUpdates(listener);
        }
        catch (Exception)
        {
            // Already removed, or the permission went away with the listener.
        }
    }

    private static Position From(Location location) => new(
        location.Latitude,
        location.Longitude,
        location.HasAccuracy ? location.Accuracy : double.NaN,
        location.HasAltitude ? location.Altitude : null,
        location.HasSpeed ? location.Speed : null,
        location.HasBearing ? location.Bearing : null,
        DateTimeOffset.FromUnixTimeMilliseconds(location.Time));

    private sealed class Listener(Action<Position> onFix) : Java.Lang.Object, ILocationListener
    {
        public void OnLocationChanged(Location location) => onFix(From(location));

        public void OnProviderDisabled(string provider)
        {
        }

        public void OnProviderEnabled(string provider)
        {
        }

        public void OnStatusChanged(string? provider, [global::Android.Runtime.GeneratedEnum] Availability status, Bundle? extras)
        {
        }
    }
}
