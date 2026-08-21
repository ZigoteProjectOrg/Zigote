using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Battery;

/// <summary>
///     Desktop implementation — three unrelated sources, one shape: Linux reads sysfs, Windows
///     asks the power manager, macOS parses <c>pmset</c>. A machine with no battery answers
///     <see cref="BatteryReading.None" />, which is the normal answer for a tower.
///     <para>
///         ponytail: no power-saver flag on desktop. GNOME's power-saver profile lives on D-Bus
///         and macOS's low-power mode behind another <c>pmset</c> call; Windows is the one that
///         hands it over for free. Add the others when an app throttles work on desktop.
///     </para>
/// </summary>
internal static class BatteryDriver
{
    public static BatteryReading Read()
    {
        if (OperatingSystem.IsLinux()) return ReadLinux();
        if (OperatingSystem.IsWindows()) return ReadWindows();
        if (OperatingSystem.IsMacOS()) return ParsePmset(Run("pmset", "-g batt"));
        return BatteryReading.None;
    }

    // ---- Linux: /sys/class/power_supply ----------------------------------------------------

    private static BatteryReading ReadLinux()
    {
        const string root = "/sys/class/power_supply";
        if (!Directory.Exists(root)) return BatteryReading.None;

        foreach (string supply in Directory.EnumerateDirectories(root))
        {
            // Mains adapters and USB ports live here too; only "Battery" is one.
            if (Read(supply, "type") is not "Battery") continue;
            return ParseLinux(Read(supply, "capacity"), Read(supply, "status"));
        }

        return BatteryReading.None;

        static string? Read(string dir, string file)
        {
            try
            {
                string path = Path.Combine(dir, file);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception)
            {
                // sysfs entries vanish when a battery is hot-unplugged mid-read.
                return null;
            }
        }
    }

    /// <summary>sysfs <c>capacity</c> + <c>status</c> — the two files every battery driver exposes.</summary>
    internal static BatteryReading ParseLinux(string? capacity, string? status)
    {
        int percent = int.TryParse(capacity, out int value) ? Math.Clamp(value, 0, 100) : -1;
        var charge = status switch
        {
            "Charging" => ChargeStatus.Charging,
            "Discharging" => ChargeStatus.Discharging,
            "Full" => ChargeStatus.Full,
            // "Not charging" is a plugged-in battery the firmware is holding — not discharging.
            "Not charging" => ChargeStatus.Unknown,
            _ => ChargeStatus.Unknown
        };
        return new BatteryReading(percent, charge, SaverOn: false);
    }

    // ---- Windows: GetSystemPowerStatus -----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [SupportedOSPlatform("windows")]
    private static BatteryReading ReadWindows()
    {
        if (!GetSystemPowerStatus(out var status)) return BatteryReading.None;

        const byte noBattery = 128, unknownFlag = 255, unknownPercent = 255;
        if (status.BatteryFlag is noBattery or unknownFlag && status.BatteryLifePercent == unknownPercent)
            return BatteryReading.None;

        int percent = status.BatteryLifePercent == unknownPercent ? -1 : status.BatteryLifePercent;
        var charge = (status.BatteryFlag & 8) != 0 ? ChargeStatus.Charging   // BATTERY_FLAG_CHARGING
            : status.AcLineStatus == 1 ? ChargeStatus.Full                   // on mains, not filling
            : ChargeStatus.Discharging;
        // SYSTEM_STATUS_FLAG: 1 = battery saver is on (Windows 10 and later).
        return new BatteryReading(percent, charge, SaverOn: status.SystemStatusFlag == 1);
    }

    // ---- macOS: pmset ----------------------------------------------------------------------

    /// <summary>
    ///     A <c>pmset -g batt</c> line reads
    ///     <c>-InternalBattery-0 (id=1234)  87%; discharging; 3:21 remaining present: true</c>.
    /// </summary>
    internal static BatteryReading ParsePmset(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return BatteryReading.None;

        foreach (string line in output.Split('\n'))
        {
            int mark = line.IndexOf('%');
            if (mark < 0) continue;

            int start = mark;
            while (start > 0 && char.IsAsciiDigit(line[start - 1])) start--;
            if (!int.TryParse(line.AsSpan(start, mark - start), out int percent)) continue;

            var charge = line.Contains("; charging", StringComparison.OrdinalIgnoreCase) ? ChargeStatus.Charging
                : line.Contains("; discharging", StringComparison.OrdinalIgnoreCase) ? ChargeStatus.Discharging
                : line.Contains("; charged", StringComparison.OrdinalIgnoreCase) ? ChargeStatus.Full
                : ChargeStatus.Unknown;
            return new BatteryReading(Math.Clamp(percent, 0, 100), charge, SaverOn: false);
        }

        return BatteryReading.None;
    }

    private static string? Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
                { RedirectStandardOutput = true, UseShellExecute = false });
            if (process is null) return null;
            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return text;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
