using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace DeviceInfo;

/// <summary>
///     iOS implementation — <see cref="UIDevice" /> and the main bundle, plus sysctl for the
///     machine code ("iPhone17,1") that <c>UIDevice.Model</c> flattens to "iPhone".
/// </summary>
internal static class DeviceInfoDriver
{
    public static DeviceProfile Get()
    {
        var device = UIDevice.CurrentDevice;
        var bundle = NSBundle.MainBundle;
        string Info(string key) => bundle.ObjectForInfoDictionary(key)?.ToString() ?? "";

        return new DeviceProfile(
            Os: $"{device.SystemName} {device.SystemVersion}",
            OsVersion: device.SystemVersion,
            SdkInt: 0,
            Model: Machine() is { Length: > 0 } machine ? machine : device.Model,
            Manufacturer: "Apple",
            Architecture: DeviceInfoPlugin.Architecture,
            // Stable per vendor while any of the vendor's apps stays installed.
            DeviceId: device.IdentifierForVendor?.AsString() ?? "",
            IsPhysicalDevice: Runtime.Arch == Arch.DEVICE,
            AppName: Info("CFBundleDisplayName") is { Length: > 0 } display
                ? display
                : Info("CFBundleName"),
            PackageId: bundle.BundleIdentifier ?? "",
            AppVersion: Info("CFBundleShortVersionString"),
            BuildNumber: Info("CFBundleVersion"));
    }

    [DllImport("libc")]
    private static extern int sysctlbyname(string name, byte[]? oldp, ref nint oldlenp, nint newp, nint newlen);

    private static string Machine()
    {
        nint len = 0;
        if (sysctlbyname("hw.machine", null, ref len, 0, 0) != 0 || len <= 0) return "";
        byte[] buf = new byte[len];
        if (sysctlbyname("hw.machine", buf, ref len, 0, 0) != 0) return "";
        return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }
}
