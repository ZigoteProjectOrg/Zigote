# Geolocation

Where the device is, once or as a stream — the `geolocator` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
Position? here = await GeolocationPlugin.GetAsync(GeoAccuracy.Fine, ct);
using var trip = GeolocationPlugin.Listen(
    fix => app.Post(() => DrawTrail(fix)), GeoAccuracy.Fine, minimumDistanceMeters: 25);
```

Location is a permission before it is an API: ask through the **Permissions** plugin first
(`ACCESS_FINE_LOCATION` / `NSLocationWhenInUseUsageDescription`). Without it every call answers
null and no updates arrive — a refused permission is an answer, not an exception. Fixes come in
on the OS's thread: post before touching widgets. Static, so no `PluginHost.Register`.

| Platform | Backend |
|---|---|
| Android | framework `LocationManager` (no Play Services dependency): GPS for `Fine`, the network provider otherwise, plus a recent last-known fix to answer instantly |
| iOS | `CLLocationManager` — `RequestLocation` for one-shot, `StartUpdatingLocation` for the stream |
| Desktop | none: `Available` is false. GeoClue2 / WinRT / CoreLocation are each a real dependency, and a wrong location is worse than none |

`minimumDistanceMeters` is applied in shared code (haversine), so the filter behaves identically
on every platform.
