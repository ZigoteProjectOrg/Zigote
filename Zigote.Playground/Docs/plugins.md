# Writing Plugins

A **plugin** packages platform channels and platform resources into a reusable unit with a lifecycle. Zigote ships a set of first-party plugins in `plugins/`:

| Plugin | Gives you |
|---|---|
| DeviceInfo, PathProvider, AppSettings | Device/app identity, standard folders, opening the system settings page |
| UrlLauncher, Share, AppLinks | Open URLs, share sheets, deep links |
| Permissions, Biometrics, SecureStorage | Runtime permissions, Face ID / fingerprint, keychain/keystore |
| Battery, Connectivity, Sensors, Geolocation | Device state and sensors |
| Notifications, Push, Tray, Haptics, Wakelock | System integration |
| FilePicker, Camera, MediaSession, WebView | Pickers, camera, now-playing controls, embedded web content |

Most have a desktop implementation and an Android/iOS one behind the same API. `plugins/Demo` shows all of them in one app.

## The contract

A plugin implements `IPlatformPlugin`: a stable `Name`, `Start()` to claim channels and resources, and `Stop()` to release them.

```csharp run height=260
// A complete plugin: claims one channel when started, releases it when stopped.
sealed class GreeterPlugin : IPlatformPlugin
{
    public string Name => "demo.greeter";

    public void Start() =>
        PlatformChannel.Handle("demo.greeter/hello", name =>
            $"Hello, {name}! (from {Environment.OSVersion.Platform}, {Environment.ProcessorCount} cores)");

    public void Stop() => PlatformChannel.Unhandle("demo.greeter/hello");
}

class Demo : ComposedWidget
{
    private readonly Signal<string> _log = new("");

    private void Say(string line) => _log.Value = $"{_log.Peek()}{line}\n";

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new Row(spacing: 8, children: [
                new AdwButton(label: "Register plugin", onPressed: () =>
                {
                    // After startup, Register starts the plugin immediately.
                    PluginHost.Register(new GreeterPlugin());
                    Say($"registered: {PluginHost.IsRegistered("demo.greeter")}");
                }),
                new AdwButton(label: "Call it", onPressed: () =>
                    Say(PlatformChannel.Invoke("demo.greeter/hello", "Ada") ?? "null: plugin not registered")) { Style = AdwButtonStyle.Suggested },
            ]),
            new Watch(() => new Label(_log.Value, fontSize: 13)),
        ]));
}
```

Press **Call it** first: `null`, because nothing implements the channel. Register, then call again.

Rules the host enforces:

- **Order.** Plugins start in registration order and stop in reverse, so a later plugin may depend on an earlier one.
- **Replacement.** Registering a plugin whose `Name` already exists replaces it. On Android the process can outlive the activity, and a relaunch registers everything again.
- **Late registration.** After startup, `Register` starts the plugin immediately.
- **Failure.** A plugin that throws in `Start` fails startup loudly; one that throws in `Stop` doesn't stop the rest of shutdown.
- **Lifecycle.** A plugin that also implements `IAppLifecycleObserver` receives pause, resume and low-memory notifications automatically.

Registration is explicit, with no assembly scanning or reflection, because Native AOT on iOS forbids both. In an app, the head registers its plugins before creating the app:

```csharp
PluginHost.Register(new NotificationsPlugin());
PluginHost.Register(new MediaSessionPlugin());
new MyApp().Run();                       // the App starts them before the first frame
```

Plugins that only answer static questions (DeviceInfo, Battery) skip registration entirely and expose a static API such as `DeviceInfoPlugin.Get()`.

## Packaging a cross-platform plugin

Generate the layout instead of hand-building it:

```sh
zigote create plugin BatteryLevel --platforms android,ios
```

A plugin is one project that **multi-targets**. NuGet and the SDK pick the implementation matching the app being built, so there's no runtime loader and no manifest:

```
Acme.Zigote.Battery/
├── Acme.Zigote.Battery.csproj      TargetFrameworks: net10.0;net10.0-android;net10.0-ios
├── BatteryPlugin.cs                shared API: what app code calls
└── Platforms/
    ├── Desktop/BatteryDriver.cs    net10.0: /sys/class/power_supply, IOKit, Win32
    ├── Android/BatteryDriver.cs    net10.0-android: BatteryManager
    └── iOS/BatteryDriver.cs        net10.0-ios: UIDevice
```

```xml
<ItemGroup>
  <Compile Remove="Platforms/**/*.cs" />
  <Compile Include="Platforms/Desktop/**/*.cs" Condition="'$(TargetFramework)' == 'net10.0'" />
  <Compile Include="Platforms/Android/**/*.cs" Condition="'$(TargetFramework)' == 'net10.0-android'" />
  <Compile Include="Platforms/iOS/**/*.cs"     Condition="'$(TargetFramework)' == 'net10.0-ios'" />
</ItemGroup>
```

Each target compiles exactly one `Platforms/` folder, and every implementation uses the same class name, so the shared code binds to it statically. Kotlin or Swift sources ride along through the usual .NET for Android/iOS mechanisms (`AndroidJavaSource`, xcframework bindings) when the implementation needs them.

> On a machine without the Android/iOS workloads, build with `-p:ZigoteMobileTargets=false` to compile only the desktop leg of every plugin.

## Native plugins on desktop

SDKs written in C, C++, Zig or Rust can be loaded at runtime as a shared library:

```csharp
PluginHost.LoadNative("plugins/libdiscord_bridge.so");
```

The library exports `zigote_plugin_init(const ZigoteHostApi* api)`, which receives function pointers to register channels, send events and query the registry, and optionally `zigote_plugin_shutdown()`. A library without a shutdown export is never unloaded, since its handlers might still be registered. This is desktop-only: iOS forbids loading code at runtime, and on Android native code arrives through the app's own build.

## Designing a good plugin API

- **Expose types, not channels.** App code should call `await Share.Send(text)`, not build payload strings.
- **Never throw for "unsupported here".** Return a value that says so (`null`, `Available == false`, `BatteryReading.None`).
- **Snapshot or stream, deliberately.** Battery level is a snapshot you poll; connectivity is a stream you listen to. Pick one per feature and say which.
- **Name channels by owner and topic** (`acme.battery/level`), and use the prefix as the plugin's `Name`.
- **Test without a device.** Channels are plain functions, so a test can `Handle` the platform side and assert on what the shared API does.
