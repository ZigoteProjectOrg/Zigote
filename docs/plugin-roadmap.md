# Plugin roadmap

What needs building as `IPlatformPlugin` packages (see [plugins.md](plugins.md)), refactored from
the Flutter-package wishlist. Items that are *not* platform plugins are called out at the bottom —
they belong in the framework, not here.

Already covered — do not build: `shared_preferences` → **Zigote.Preferences**, camera →
**plugins/Camera**, audio/video → **Zigote.Audioplayer / Zigote.Videoplayer**, sqlite →
**Zigote.Persistence.SQLite**.

**Built** (see `plugins/`): DeviceInfo, PathProvider, UrlLauncher, Permissions, FilePicker,
Notifications, Tray, WebView, Share, Battery, Haptics, AppSettings, Connectivity, SecureStorage,
Geolocation, Sensors — every slot below. Timbre consumes them: its hand-rolled
StatusNotifierItem, notification transport, XDG paths, url opening and Android SystemDialogs were
replaced by the plugins.

Nothing on the original list is unbuilt, and Round two's push / biometrics / deep-link trio and
its two list widgets are done too. What is left is depth, not coverage: desktop backends
for Geolocation (GeoClue2 / WinRT / CoreLocation) and Sensors (Linux IIO), a native share sheet on
Windows and macOS, and Keychain via SecItem interop instead of the `security` tool. A second pass
over the pub.dev top list — [Round two](#round-two--what-a-pass-over-the-pubdev-top-list-still-exposes)
— is where the remaining real gaps are: barcode scanning, in-app purchase, Lottie, forms.

## Tier 1 — almost every app needs these

| Plugin | Flutter equivalent | Notes |
|---|---|---|
| ~~`Zigote.Plugins.PathProvider`~~ | path_provider | **Built** (plugins/PathProvider): `Environment.GetFolderPath` on desktop, one channel each on Android/iOS. |
| ~~`Zigote.Plugins.UrlLauncher`~~ | url_launcher | **Built** (plugins/UrlLauncher): `Process.Start` with the shell on desktop, `ACTION_VIEW` on Android, `UIApplication.open` on iOS. |
| ~~`Zigote.Plugins.SecureStorage`~~ | flutter_secure_storage | **Built** (plugins/SecureStorage): Android Keystore AES-GCM, iOS/macOS Keychain, Secret Service on Linux, DPAPI on Windows. No plaintext fallback — no keystore means `Available` is false. |
| ~~`Zigote.Plugins.Connectivity`~~ | connectivity_plus | **Built** (plugins/Connectivity): `System.Net.NetworkInformation` on desktop, `ConnectivityManager` callbacks on Android, `NWPathMonitor` on iOS. |
| ~~`Zigote.Plugins.Permissions`~~ | permission_handler | **Built** (plugins/Permissions): ask once, serialized, all-or-nothing; desktop answers granted. |
| ~~`Zigote.Plugins.Share`~~ | share_plus | **Built** (plugins/Share): chooser + cache-backed file provider on Android, `UIActivityViewController` on iOS, clipboard fallback on desktop. |

## Tier 2 — common, build on demand

| Plugin | Flutter equivalent | Notes |
|---|---|---|
| ~~`Zigote.Plugins.FilePicker`~~ | image_picker + file_picker | **Built** (plugins/FilePicker): `FileDialog` on desktop, the Storage Access Framework on Android. |
| ~~`Zigote.Plugins.Notifications`~~ | flutter_local_notifications | **Built** (plugins/Notifications): one channel per app on Android, `UNUserNotificationCenter` on iOS, the freedesktop spec on Linux. |
| ~~`Zigote.Plugins.Haptics`~~ | vibration | **Built** (plugins/Haptics): `VibrationEffect` waveforms on Android, the three UIFeedbackGenerator families on iOS, no-op on desktop. |
| ~~`Zigote.Plugins.DeviceInfo`~~ | device_info_plus + package_info_plus | **Built** (plugins/DeviceInfo): device and app identity in one profile record. |
| ~~`Zigote.Plugins.AppSettings`~~ | app_settings | **Built** (plugins/AppSettings): app, notification and location pages on Android; the app page on iOS; nothing to open on desktop. |
| ~~`Zigote.Plugins.Battery`~~ | battery_plus | **Built** (plugins/Battery): sysfs / `GetSystemPowerStatus` / `pmset` on desktop, `BatteryManager` on Android, `UIDevice` on iOS. |

## Tier 3 — heavy or niche, only with a driving app

| Plugin | Flutter equivalent | Notes |
|---|---|---|
| ~~`Zigote.Plugins.WebView`~~ | webview_flutter | **Built** (plugins/WebView), built for JS web extensions (maps, payments): a `window.zigote` message bridge both ways, document-start user scripts, a navigation filter, progress/history/failure events and `ClearBrowsingDataAsync`. Overlay native views on Windows/X11/Android/iOS; native Wayland renders the page into an engine texture (damage-driven, SIMD conversion). |
| ~~`Zigote.Plugins.Geolocation`~~ | geolocator | **Built** (plugins/Geolocation): framework `LocationManager` on Android, `CLLocationManager` on iOS, shared haversine distance filter. Desktop unavailable. |
| ~~`Zigote.Plugins.Sensors`~~ | sensors_plus | **Built** (plugins/Sensors): `SensorManager` on Android, `CMMotionManager` on iOS, units normalised to m/s² · rad/s · µT. Desktop unavailable. |
| ~~`Zigote.Plugins.Tray`~~ | tray_manager | **Built** (plugins/Tray): desktop-only; status icon + menu. |

Every built plugin is exercised by one app: [`plugins/Demo`](../plugins/Demo/README.md) —
`dotnet run --project plugins/Demo/PluginsDemo`.

## Round two — what a pass over the pub.dev top list still exposes

Reviewed against the pub.dev "top" ranking (August 2026, first ~50) plus the Flutter Favorites.
Most of that list is already answered here or by the BCL (see the last table); what follows is
what genuinely has no answer in Zigote today, ranked by how many real apps hit the wall.

### Tier A — build these next (plugins)

| Plugin | pub.dev equivalent | Why it is a real gap | Cost |
|---|---|---|---|
| ~~`Zigote.Plugins.Push`~~ | firebase_messaging | **Built** (plugins/Push), Firebase-free: the plugin owns registration, the token and the message stream; the transport feeds it over two channels, so FCM, HMS or your own socket all work. Real APNs registration on iOS. | Done. |
| ~~`Zigote.Plugins.Biometrics`~~ | local_auth | **Built** (plugins/Biometrics): framework `BiometricPrompt` on Android (no AndroidX), `LAContext` on iOS, unavailable on desktop until Windows Hello is wired. | Done. |
| ~~`Zigote.Plugins.AppLinks`~~ | app_links / uni_links | **Built** (plugins/AppLinks): argv plus a named-pipe single-instance handoff on desktop (a second launch feeds the running app and exits), head-forwarded intents and user activities on mobile. | Done. |
| `Zigote.Plugins.BarcodeScanner` | mobile_scanner | Ticket scanning, QR sign-in, inventory. The Camera plugin already delivers frames; what is missing is a decoder. A zxing-cpp binding under `native/` decodes on every platform at once and avoids ML Kit's Play Services dependency. | Medium — one native binding, then a widget over Camera frames. |
| `Zigote.Plugins.InAppPurchase` | in_app_purchase | The money path: Play Billing v7 and StoreKit 2. Nothing else can substitute for it, and receipt validation has to be done right. No lazy version — a half-built billing flow charges real cards. | Large. Build it when an app ships a paid tier. |
| `Zigote.Plugins.Wakelock` | wakelock_plus | Keep the screen awake during video, navigation or a long game session. `FLAG_KEEP_SCREEN_ON`, `idleTimerDisabled`, the screensaver inhibit on desktop. | Tiny — an afternoon. |
| `Zigote.Plugins.InAppReview` | in_app_review | Ask for a store rating without leaving the app. One API call per platform, no-op on desktop. | Tiny. |

### Tier B — framework features, not packages

| Feature | pub.dev equivalent | Note |
|---|---|---|
| Lottie playback | lottie | The one animation format apps receive from designers and Zigote cannot play. Same shape as the SVG answer: bind rlottie under `native/`, expose a `LottieView` widget, and compile animations ahead of time. Highest-value item in this table. |
| Network + Google fonts | google_fonts | Fonts today are bundled at build time. A `FontSource.Network` that fetches through Zigote.Http and caches next to the asset cache covers the google_fonts use case in a fraction of the code. |
| ~~Paged / infinite lists~~ | infinite_scroll_pagination | **Built**: `PagedListView<T>` next to `ListView` — an empty page ends the list, a failed page stops the loop and offers a retry, the loading footer is a `Skeleton`. |
| Forms and validation | flutter_form_builder, reactive_forms | There is no `Form`, no validator vocabulary, no "validate on submit and focus the first error". Every input widget exists; the layer that binds them to a model does not. |
| ~~Staggered grid~~ | flutter_staggered_grid_view | **Built**: `StaggeredGrid` — fixed columns, each tile keeps its height and lands in the shortest column. |
| Carousel in core | carousel_slider | `Carousel` exists in Zigote.UI.Adwaita only; a paged, snapping viewport is a core-layout concern, not an Adwaita one. |
| PDF generation + printing | pdf + printing | Invoices, reports, tickets. Two halves: a document builder (managed, no platform code) and a print/share step that the Share plugin already half-covers. |

### Tier C — observability, once an app is in the field

| Feature | pub.dev equivalent | Note |
|---|---|---|
| Crash and error reporting | sentry_flutter, firebase_crashlytics | `AppLog` already catches unhandled exceptions locally; what is missing is a sink that batches them to an endpoint (Zigote.Http is right there) with a device profile from the DeviceInfo plugin attached. Keep the backend pluggable — no vendor in the framework. |
| Analytics events | firebase_analytics | A thin `Analytics.Track(name, properties)` over the same pluggable sink. Deliberately small: the value is the one call site shape, not a vendor SDK. |
| Speech to text / text to speech | speech_to_text, flutter_tts | Real platform APIs on both mobile OSes, niche until an app asks. |
| Background work | workmanager, flutter_background_service | WorkManager on Android, BGTaskScheduler on iOS. Heavy, and the OS rules differ enough that the shared API is mostly a lie. Only with a driving app. |

### Never build — .NET already answers these

Roughly half the pub.dev top list is Dart filling gaps the BCL does not have.

| pub.dev | Zigote / .NET answer |
|---|---|
| http, dio, retry | **Zigote.Http** — requests as values, middleware, `RetryMiddleware`, cache |
| intl | `System.Globalization` + **Zigote.UI.Localizations** |
| uuid | `Guid` |
| crypto, encrypt | `System.Security.Cryptography` |
| path | `System.IO.Path` |
| collection | LINQ |
| equatable | records |
| json_serializable, freezed | records + `System.Text.Json` source generation |
| build_runner | Roslyn source generators (**Zigote.Generators**) |
| logging | **Zigote.Logging** |
| mocktail, mockito | xUnit + NSubstitute |
| flutter_lints | `.editorconfig` + Roslyn analyzers |
| win32 | P/Invoke |
| mime | already in `FilePicker` / `Zigote.Mcp` |
| dotenv | `Environment` + configuration binding |
| rxdart | **Zigote.Reactive.R3** |
| provider, riverpod, get_it | **Zigote.Bloc** + the reactive core + `Microsoft.Extensions.DependencyInjection` |
| sqflite, hive, isar | **Zigote.Persistence.SQLite** / **Zigote.Preferences** |
| shimmer | `Skeleton` in Zigote.UI |
| animations | the transitions and `Animate` effects in Zigote.UI |
| cupertino_icons, font_awesome_flutter | the bundled icon fonts + asset/font tree shaking |
| firebase_core | nothing to answer — Firebase is a backend choice, not a framework feature |

## Not plugins — framework features

- **skeletonizer** → **Built**: `Skeleton` in Zigote.UI (`Widgets/Controls/Skeleton.cs`) — a rounded
  block with a clipped highlight sweep, plus `Skeleton.Circle`/`Box`/`Text` for composing a
  placeholder that matches the real layout. Pure drawing, no platform code.
- **flutter_svg** → **Built**: `SvgPicture` + `SvgAsset` in Zigote.UI over resvg (`native/zigote-svg`), plus ahead-of-time compiled SVG. See [`svg.md`](svg.md).
- **animations** → already in Zigote.UI: implicit (`AnimatedContainer`/`Opacity`/`Align`/`Padding`/
  `Size`/`Switcher`), explicit (`Fade`/`Slide`/`ScaleTransition`, `TweenAnimationBuilder`,
  `AnimatedBuilder`), the `Animate` effects chain, and page-route transitions.
- **Clipboard, window management** → core engine surface (NativeWindow), not packages.
