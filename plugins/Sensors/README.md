# Sensors

Accelerometer, gyroscope, magnetometer and linear acceleration as streams — the `sensors_plus`
slot from the [plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
using var tilt = SensorsPlugin.Listen(SensorKind.Accelerometer,
    s => app.Post(() => Steer(s.X / 9.81)), TimeSpan.FromMilliseconds(16));

if (SensorsPlugin.IsAvailable(SensorKind.Gyroscope)) { /* … */ }
```

Units are the same everywhere: m/s² for the two acceleration kinds, rad/s for the gyroscope,
microtesla for the magnetometer (iOS reports g and is scaled here). Samples arrive on the OS's
sensor thread at 50–200 Hz — post before touching widgets, and keep the handler cheap. The
platform sensor is switched on with the first listener for that kind and off with the last, so a
forgotten `Dispose` is a battery bill. Subscribing to a sensor the device lacks is allowed and
simply never fires. Static, so no `PluginHost.Register`.

| Platform | Backend |
|---|---|
| Android | `SensorManager`, one listener per kind; the interval is passed through as the sampling period |
| iOS | one `CMMotionManager`; linear acceleration comes from device motion's `UserAcceleration` |
| Desktop | none: `IsAvailable` is false for every kind |
