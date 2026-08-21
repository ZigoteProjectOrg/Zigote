namespace Battery;

/// <summary>What the battery is doing right now.</summary>
public enum ChargeStatus
{
    /// <summary>The platform will not say — treat as "on mains" for UI purposes.</summary>
    Unknown,

    /// <summary>Plugged in and filling.</summary>
    Charging,

    /// <summary>Running off the battery.</summary>
    Discharging,

    /// <summary>Plugged in and full.</summary>
    Full,

    /// <summary>No battery at all — a desktop tower, a plugged-in dev board.</summary>
    NotPresent
}

/// <summary>
///     One reading of the battery.
/// </summary>
/// <param name="Percent">0–100, or -1 when there is no battery or no reading.</param>
/// <param name="Status">Charging, discharging, full, absent.</param>
/// <param name="SaverOn">The OS power-saving mode is on — back off background work while it is.</param>
public readonly record struct BatteryReading(int Percent, ChargeStatus Status, bool SaverOn)
{
    /// <summary>No battery, no reading — what a desktop tower answers.</summary>
    public static readonly BatteryReading None = new(-1, ChargeStatus.NotPresent, false);

    /// <summary>Whether the device has a battery to read at all.</summary>
    public bool Present => Status != ChargeStatus.NotPresent;
}

/// <summary>
///     Battery — level, charge status and power-saver state, the <c>battery_plus</c> slot from
///     the plugin roadmap. Static, nothing to register with <c>PluginHost</c>.
///     <para>
///         ponytail: a snapshot, not a stream. Battery level moves in minutes, so an app that
///         shows it polls <see cref="Read" /> on a timer (or on resume). Add change events when
///         something needs to react inside a second.
///     </para>
/// </summary>
public static class BatteryPlugin
{
    /// <summary>Read the battery now. Never throws: an unreadable battery is <see cref="BatteryReading.None" />.</summary>
    public static BatteryReading Read()
    {
        try
        {
            return BatteryDriver.Read();
        }
        catch (Exception)
        {
            return BatteryReading.None;
        }
    }
}
