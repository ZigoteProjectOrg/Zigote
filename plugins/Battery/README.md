# Battery

Battery level, charge status and power-saver state — the `battery_plus` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
BatteryReading battery = BatteryPlugin.Read();
// battery.Percent   87, or -1 when there is no reading
// battery.Status    Charging / Discharging / Full / NotPresent / Unknown
// battery.SaverOn   the OS is in power-saving mode — back off background work
// battery.Present   false on a desktop tower
```

A snapshot, not a stream: battery level moves in minutes, so poll on a timer or on resume.
Never throws; static, so no `PluginHost.Register`.

| Platform | Source | Power saver |
|---|---|---|
| Linux | `/sys/class/power_supply/*` (`type`, `capacity`, `status`) | not read |
| Windows | `GetSystemPowerStatus` | `SYSTEM_STATUS_FLAG` |
| macOS | `pmset -g batt` | not read |
| Android | `BatteryManager` + the sticky `ACTION_BATTERY_CHANGED` broadcast | `PowerManager.IsPowerSaveMode` |
| iOS | `UIDevice` with battery monitoring on | `NSProcessInfo.LowPowerModeEnabled` |
