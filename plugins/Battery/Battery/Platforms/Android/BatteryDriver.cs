using Android.App;
using Android.Content;
using Android.OS;

namespace Battery;

/// <summary>
///     Android implementation — <see cref="BatteryManager" /> for the level, a sticky
///     <c>ACTION_BATTERY_CHANGED</c> broadcast (registered with a null receiver, which returns
///     the last one without subscribing) for the charge status.
/// </summary>
internal static class BatteryDriver
{
    public static BatteryReading Read()
    {
        var context = Application.Context;
        var manager = (BatteryManager?)context.GetSystemService(Context.BatteryService);
        int percent = manager?.GetIntProperty((int)BatteryProperty.Capacity) ?? -1;
        if (percent is < 0 or > 100) percent = -1;

        var status = ChargeStatus.Unknown;
        using (var sticky = context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged)))
        {
            if (sticky is not null)
            {
                status = (BatteryStatus)sticky.GetIntExtra(BatteryManager.ExtraStatus, -1) switch
                {
                    BatteryStatus.Charging => ChargeStatus.Charging,
                    BatteryStatus.Full => ChargeStatus.Full,
                    BatteryStatus.Discharging or BatteryStatus.NotCharging => ChargeStatus.Discharging,
                    _ => ChargeStatus.Unknown
                };
                if (!sticky.GetBooleanExtra(BatteryManager.ExtraPresent, true))
                    return BatteryReading.None;
            }
        }

        var power = (PowerManager?)context.GetSystemService(Context.PowerService);
        return new BatteryReading(percent, status, SaverOn: power?.IsPowerSaveMode ?? false);
    }
}
