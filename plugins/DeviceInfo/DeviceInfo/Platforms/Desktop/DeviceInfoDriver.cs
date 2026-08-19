namespace DeviceInfo;

/// <summary>
///     Desktop implementation. Linux reads the standard files — os-release for the distro
///     name, DMI for model/vendor, device-tree as the ARM-board fallback, machine-id for the
///     device identifier. Windows answers the id from the registry; macOS from what the
///     runtime already knows.
/// </summary>
internal static class DeviceInfoDriver
{
    public static DeviceProfile Get()
    {
        string osVersion = Environment.OSVersion.Version.ToString();

        if (OperatingSystem.IsLinux())
        {
            string os = ParsePrettyName(ReadLinesOrEmpty("/etc/os-release", "/usr/lib/os-release"))
                        ?? $"Linux {osVersion}";
            // DMI covers PCs; ARM boards (Raspberry Pi et al.) publish a device-tree model instead.
            string model = ReadTrimmedOrEmpty("/sys/devices/virtual/dmi/id/product_name");
            if (model.Length == 0) model = ReadTrimmedOrEmpty("/proc/device-tree/model").TrimEnd('\0');
            return Desktop(os, osVersion) with {
                Model = model,
                Manufacturer = ReadTrimmedOrEmpty("/sys/devices/virtual/dmi/id/sys_vendor"),
                DeviceId = ReadTrimmedOrEmpty("/etc/machine-id") is { Length: > 0 } id
                    ? id
                    : ReadTrimmedOrEmpty("/var/lib/dbus/machine-id"),
            };
        }

        if (OperatingSystem.IsWindows())
            return Desktop($"Windows {osVersion}", osVersion) with { DeviceId = WindowsMachineGuid() };

        // ponytail: no hardware model/vendor on Windows (registry) or macOS (sysctl), no macOS
        // device id (IOPlatformUUID needs IOKit) — add the platform lookup when an app needs it.
        string osName = OperatingSystem.IsMacOS() ? $"macOS {osVersion}"
            : System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        return Desktop(osName, osVersion);
    }

    private static DeviceProfile Desktop(string os, string osVersion) => new(
        Os: os, OsVersion: osVersion, SdkInt: 0, Model: "", Manufacturer: "",
        Architecture: DeviceInfoPlugin.Architecture, DeviceId: "", IsPhysicalDevice: true,
        AppName: "", PackageId: "", AppVersion: "", BuildNumber: "");

    private static string WindowsMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                keyName: @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                valueName: "MachineGuid",
                defaultValue: null) as string ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>The PRETTY_NAME value from os-release lines, unquoted; null when absent.</summary>
    internal static string? ParsePrettyName(IEnumerable<string> osReleaseLines)
    {
        const string key = "PRETTY_NAME=";
        foreach (string line in osReleaseLines)
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                string value = line[key.Length..].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
        return null;
    }

    private static IEnumerable<string> ReadLinesOrEmpty(params string[] candidates)
    {
        foreach (string path in candidates)
            try
            {
                if (File.Exists(path)) return File.ReadLines(path);
            }
            catch (IOException) { }
        return [];
    }

    private static string ReadTrimmedOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException) // DMI files can be unreadable in sandboxes/containers
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }
}
