using System.Reflection;
using System.Runtime.InteropServices;

namespace DeviceInfo;

/// <summary>
///     One immutable snapshot of what this device and app are. Fields the platform cannot
///     answer are empty strings, never null.
/// </summary>
/// <param name="Os">Human-readable OS name and release — "Fedora Linux 44 (Workstation Edition)", "Android 15", "iOS 18.2".</param>
/// <param name="OsVersion">The numeric version behind it — kernel version on Linux, release on mobile.</param>
/// <param name="SdkInt">Android API level (Build.VERSION.SDK_INT); 0 everywhere else.</param>
/// <param name="Model">Hardware model — DMI product name, Build.MODEL, the iOS machine code ("iPhone17,1").</param>
/// <param name="Manufacturer">Hardware vendor — DMI sys_vendor, Build.MANUFACTURER, "Apple" on iOS.</param>
/// <param name="Architecture">Process CPU architecture — "X64", "Arm64".</param>
/// <param name="DeviceId">
///     Stable per-device (Linux machine-id, Windows MachineGuid, Android ANDROID_ID) or
///     per-vendor-install (iOS identifierForVendor) identifier. Privacy-sensitive — put it in
///     diagnostics, not in tracking. Empty on macOS.
/// </param>
/// <param name="IsPhysicalDevice">False on the Android emulator and the iOS simulator; true on desktop.</param>
/// <param name="AppName">Display name — the Android app label, CFBundleDisplayName, or the entry assembly's name.</param>
/// <param name="PackageId">Android package name / iOS bundle identifier; empty on desktop.</param>
/// <param name="AppVersion">Store-facing version: PackageInfo.versionName / CFBundleShortVersionString; assembly informational version on desktop.</param>
/// <param name="BuildNumber">Android longVersionCode / CFBundleVersion; empty on desktop.</param>
public sealed record DeviceProfile(
    string Os,
    string OsVersion,
    int SdkInt,
    string Model,
    string Manufacturer,
    string Architecture,
    string DeviceId,
    bool IsPhysicalDevice,
    string AppName,
    string PackageId,
    string AppVersion,
    string BuildNumber);

/// <summary>
///     DeviceInfo — device and app identity for Zigote. Everything here is a static fact, so
///     there is nothing to register with <c>PluginHost</c>: call <see cref="Get" /> from
///     anywhere, any thread. The csproj compiles exactly one <c>Platforms/DeviceInfoDriver</c>
///     per target framework — the desktop build covers Linux (os-release, DMI, device-tree,
///     machine-id), Windows and macOS.
/// </summary>
public static class DeviceInfoPlugin
{
    private static DeviceProfile? _cached;

    /// <summary>The device and app profile. Computed once, then cached — the facts don't move.</summary>
    public static DeviceProfile Get()
    {
        if (_cached is not null) return _cached;
        var p = DeviceInfoDriver.Get();
        // Platforms without a package system answer from the entry assembly.
        if (p.AppVersion.Length == 0) p = p with { AppVersion = AssemblyVersion() };
        if (p.AppName.Length == 0)
            p = p with { AppName = Assembly.GetEntryAssembly()?.GetName().Name ?? "" };
        return _cached = p;
    }

    private static string AssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        string? informational = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Informational versions carry "+<commit>" build metadata; the version is the part before it.
        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }
        return asm?.GetName().Version?.ToString() ?? "";
    }

    internal static string Architecture => RuntimeInformation.ProcessArchitecture.ToString();
}
