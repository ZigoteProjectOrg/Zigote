using Foundation;
using UIKit;

namespace Battery;

/// <summary>
///     iOS implementation — <see cref="UIDevice" /> with battery monitoring switched on (off by
///     default, and off it answers -1 for everything), plus <c>NSProcessInfo</c> for Low Power
///     Mode. The simulator has no battery and says so.
/// </summary>
internal static class BatteryDriver
{
    public static BatteryReading Read()
    {
        var device = UIDevice.CurrentDevice;
        device.BatteryMonitoringEnabled = true;

        var status = device.BatteryState switch
        {
            UIDeviceBatteryState.Charging => ChargeStatus.Charging,
            UIDeviceBatteryState.Full => ChargeStatus.Full,
            UIDeviceBatteryState.Unplugged => ChargeStatus.Discharging,
            _ => ChargeStatus.Unknown
        };
        // BatteryLevel is 0..1, or -1 when the level is unknown (simulator, monitoring off).
        int percent = device.BatteryLevel < 0 ? -1 : (int)Math.Round(device.BatteryLevel * 100);
        return new BatteryReading(percent, status, NSProcessInfo.ProcessInfo.LowPowerModeEnabled);
    }
}
