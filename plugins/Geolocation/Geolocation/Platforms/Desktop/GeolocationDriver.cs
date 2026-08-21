namespace Geolocation;

/// <summary>
///     Desktop implementation — none. Each desktop keeps location behind a different service:
///     GeoClue2 on D-Bus (and only for apps the portal knows), WinRT's Geolocator with its own
///     capability declaration, CoreLocation on macOS. None of them is reachable from here
///     without a real dependency, and a wrong location is worse than no location.
///     <para>
///         ponytail: answers unavailable. Wire GeoClue2 / WinRT / CoreLocation when a desktop
///         app actually needs to know where it is.
///     </para>
/// </summary>
internal static class GeolocationDriver
{
    public static bool Available => false;

    public static Task<Position?> GetAsync(GeoAccuracy accuracy, CancellationToken cancellationToken)
        => Task.FromResult<Position?>(null);

    public static void StartUpdates(GeoAccuracy accuracy, Action<Position> publish)
    {
    }

    public static void StopUpdates()
    {
    }
}
