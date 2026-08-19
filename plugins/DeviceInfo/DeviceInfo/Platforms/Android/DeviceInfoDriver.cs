using Android.OS;
using Android.Provider;

namespace DeviceInfo;

/// <summary>
///     Android implementation — hardware from <see cref="Build" /> constants, identity from
///     Settings.Secure and the package manager.
/// </summary>
internal static class DeviceInfoDriver
{
    public static DeviceProfile Get()
    {
        var ctx = Android.App.Application.Context;
        var pm = ctx.PackageManager;
        var package = pm is not null && ctx.PackageName is { } name
            ? pm.GetPackageInfo(name, 0)
            : null;

        return new DeviceProfile(
            Os: $"Android {Build.VERSION.Release}",
            OsVersion: Build.VERSION.Release ?? "",
            SdkInt: (int)Build.VERSION.SdkInt,
            Model: Build.Model ?? "",
            Manufacturer: Build.Manufacturer ?? "",
            Architecture: DeviceInfoPlugin.Architecture,
            // Per-app-signing-key since API 26 — resets on factory reset, not on reinstall.
            DeviceId: Settings.Secure.GetString(ctx.ContentResolver, Settings.Secure.AndroidId) ?? "",
            IsPhysicalDevice: !IsEmulator(),
            AppName: ctx.ApplicationInfo?.LoadLabel(pm!)?.ToString() ?? "",
            PackageId: ctx.PackageName ?? "",
            AppVersion: package?.VersionName ?? "",
            BuildNumber: BuildNumber(package));
    }

    private static string BuildNumber(Android.Content.PM.PackageInfo? package)
    {
        if (package is null) return "";
        // LongVersionCode is API 28; the plugin's floor is 26.
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) return package.LongVersionCode.ToString();
#pragma warning disable CS0618 // VersionCode — the pre-28 API is the point
        return package.VersionCode.ToString();
#pragma warning restore CS0618
    }

    /// <summary>The device_info_plus heuristic, condensed: emulators wear one of these markers.</summary>
    private static bool IsEmulator()
    {
        string fingerprint = Build.Fingerprint ?? "";
        string model = Build.Model ?? "";
        string product = Build.Product ?? "";
        return fingerprint.StartsWith("generic") || fingerprint.StartsWith("unknown")
               || model.Contains("google_sdk") || model.Contains("Emulator")
               || model.Contains("sdk_gphone") || model.Contains("Android SDK built for")
               || product.Contains("sdk") || Build.Hardware is "goldfish" or "ranchu";
    }
}
