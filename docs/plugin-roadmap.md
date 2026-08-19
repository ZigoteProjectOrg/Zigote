# Plugin roadmap

What needs building as `IPlatformPlugin` packages (see [plugins.md](plugins.md)), refactored from
the Flutter-package wishlist. Items that are *not* platform plugins are called out at the bottom —
they belong in the framework, not here.

Already covered — do not build: `shared_preferences` → **Zigote.Preferences**, camera →
**plugins/Camera**, audio/video → **Zigote.Audioplayer / Zigote.Videoplayer**, sqlite →
**Zigote.Persistence.SQLite**.

## Tier 1 — almost every app needs these

| Plugin | Flutter equivalent | Notes |
|---|---|---|
| `Zigote.Plugins.PathProvider` | path_provider | Tiny: `Environment.GetFolderPath` on desktop, one channel each on Android/iOS. |
| `Zigote.Plugins.UrlLauncher` | url_launcher | `xdg-open`/`ShellExecute`/`NSWorkspace` on desktop, Intent/`UIApplication.open` on mobile. |
| `Zigote.Plugins.SecureStorage` | flutter_secure_storage | Keystore / Keychain / libsecret / DPAPI. Security path — no lazy fallback to plaintext. |
| `Zigote.Plugins.Connectivity` | connectivity_plus | Status + change events via `JsonEvents<T>`. Could live inside Zigote.Network instead of a separate package. |
| `Zigote.Plugins.Permissions` | permission_handler | The `Request` channel pattern is built for this (activity results, dialogs). Mostly mobile; desktop answers "granted". |
| `Zigote.Plugins.Share` | share_plus | Docs already use `ShareArgs` as the worked example — make it real. |

## Tier 2 — common, build on demand

| Plugin | Flutter equivalent | Notes |
|---|---|---|
| `Zigote.Plugins.FilePicker` | image_picker + file_picker | One plugin: open/save dialogs on desktop (portals on Linux), photo/document pickers on mobile. Don't split image_picker out. |
| `Zigote.Plugins.Notifications` | flutter_local_notifications | Named in plugins.md as an internal system to build on this pattern. |
| `Zigote.Plugins.Haptics` | vibration | `Vibrator` / `UIFeedbackGenerator`; no-op on desktop. |
| `Zigote.Plugins.DeviceInfo` | device_info_plus + package_info_plus | One plugin: OS version, model, app version/build. |
| `Zigote.Plugins.AppSettings` | app_settings | Open the OS settings page for the app. Pairs with Permissions ("denied → open settings"). |
| `Zigote.Plugins.Battery` | battery_plus | The worked example in plugins.md — cheap to finish. |

## Tier 3 — heavy or niche, only with a driving app

| Plugin | Flutter equivalent | Notes |
|---|---|---|
| `Zigote.Plugins.WebView` | webview_flutter | By far the most expensive item on the list (native view embedding in the compositor). Do not start without a concrete consumer. |
| `Zigote.Plugins.Geolocation` | geolocator | |
| `Zigote.Plugins.Sensors` | sensors_plus | Accelerometer/gyro events; games may want it. |
| `Zigote.Plugins.Tray` | tray_manager | Desktop-only; status icon + menu. |

## Not plugins — framework features

- **skeletonizer** → a shimmer/skeleton widget in Zigote.UI. Pure drawing, no platform code.
- **flutter_svg** → SVG parsing/rendering in Render2D/UI. Renderer feature.
- **animations** → transition patterns in Zigote.UI. Already framework territory.
- **Clipboard, window management** → core engine surface (NativeWindow), not packages.
