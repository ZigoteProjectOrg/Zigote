using Xunit;

namespace Geolocation.Tests;

/// <summary>
///     The shared parts: the distance maths behind the filter, and the filter itself. The
///     platform drivers are the OS's code; desktop has none, which is also checked.
/// </summary>
public class GeolocationTests
{
    private static Position At(double latitude, double longitude)
        => new(latitude, longitude, 5, null, null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void DistanceMeters_MatchesKnownSeparations()
    {
        // One degree of latitude is ~111.2 km anywhere on the globe.
        Assert.InRange(GeolocationPlugin.DistanceMeters(At(0, 0), At(1, 0)), 110_000, 112_000);
        // London to Paris, ~343 km.
        Assert.InRange(
            GeolocationPlugin.DistanceMeters(At(51.5074, -0.1278), At(48.8566, 2.3522)), 338_000, 348_000);
        Assert.Equal(0, GeolocationPlugin.DistanceMeters(At(12, 34), At(12, 34)));
    }

    [Fact]
    public void Listen_DropsFixesInsideTheDistanceFilter_AndStopsOnDispose()
    {
        List<Position> seen = [];
        var subscription = GeolocationPlugin.Listen(seen.Add, minimumDistanceMeters: 100);

        GeolocationPlugin.Publish(At(50, 0));            // first fix always delivered
        GeolocationPlugin.Publish(At(50.0001, 0));       // ~11 m — dropped
        GeolocationPlugin.Publish(At(50.01, 0));         // ~1.1 km — delivered
        subscription.Dispose();
        GeolocationPlugin.Publish(At(51, 0));            // gone — not heard

        Assert.Equal([At(50, 0), At(50.01, 0)], seen);
    }

    [Fact]
    public async Task Desktop_HasNoLocation()
    {
        Assert.False(GeolocationPlugin.Available);
        Assert.Null(await GeolocationPlugin.GetAsync(
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
