# DeviceInfo

Device and app identity for Zigote — the `device_info_plus` + `package_info_plus` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md), as one package.

```csharp
var p = DeviceInfoPlugin.Get();
// p.Os               "Fedora Linux 44 (Workstation Edition)" / "Android 15" / "iOS 18.2"
// p.OsVersion        "6.14.2" / "15" / "18.2"
// p.SdkInt           Android API level (35); 0 elsewhere
// p.Model            "ThinkPad X1 Carbon Gen 11" / "Pixel 9" / "iPhone17,1"
// p.Manufacturer     "LENOVO" / "Google" / "Apple"
// p.Architecture     "X64" / "Arm64"
// p.DeviceId         machine-id / MachineGuid / ANDROID_ID / identifierForVendor ("" on macOS)
// p.IsPhysicalDevice false on the Android emulator / iOS simulator
// p.AppName          app label / CFBundleDisplayName / entry assembly name
// p.PackageId        package name / bundle id; "" on desktop
// p.AppVersion       versionName / CFBundleShortVersionString / assembly informational version
// p.BuildNumber      longVersionCode / CFBundleVersion; "" on desktop
```

`example/DeviceInfoExample` shows every field as a GNOME Settings-style About page
(Zigote.UI.Adwaita): `dotnet run --project example/DeviceInfoExample`.

Static facts, so no `PluginHost.Register` — call `Get()` from anywhere; the answer is computed
once and cached. Per-platform sources, selected at build time like every Zigote plugin:

| Platform | Os | Model / Manufacturer | DeviceId | Package identity |
|---|---|---|---|---|
| Linux | `/etc/os-release` PRETTY_NAME | DMI (`/sys/devices/virtual/dmi/id`), device-tree fallback for ARM boards | `/etc/machine-id` | entry assembly |
| Windows | runtime version | not yet (registry — add when needed) | `MachineGuid` (registry) | entry assembly |
| macOS | runtime version | not yet (sysctl — add when needed) | not yet (IOPlatformUUID needs IOKit) | entry assembly |
| Android | `Build.VERSION` | `Build.MODEL` / `Build.MANUFACTURER` | `Settings.Secure.ANDROID_ID` | `PackageManager` |
| iOS | `UIDevice` | sysctl `hw.machine` / "Apple" | `identifierForVendor` | `NSBundle` Info.plist |

`DeviceId` is privacy-sensitive: fine for diagnostics and support, not for tracking.
