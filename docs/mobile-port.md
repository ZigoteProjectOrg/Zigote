# Android / iOS port — status & plan

*2026-07-29. Companion to the touch/lifecycle work (outer `e794ba1`, engine `9927f42`) and the
build scaffolding that follows it. Read this before resuming the mobile bring-up.*

## What already works (landed)

| Layer | State |
|---|---|
| Touch input | End-to-end: SDL finger events → `EVT_TOUCH_*` 18–21 (direct devices only; touch↔mouse synthesis off) → pooled C# `TouchEvent`s → App primary-finger promotion with slop, drag-to-scroll (1:1 + fling via `SmoothScroller`), long-press → `OnLongPress` → context menus, `OnPointerCancel` across the interactive widgets. Testable on any touchscreen desktop (e.g. Windows) today. |
| Lifecycle | `EVT_APP_BACKGROUND/FOREGROUND/LOW_MEMORY` 22–24 → `AppLifecycleState` observer API; Paused fully stops layout/paint/present while still draining events; `ZigoteApp.OnPause/OnResume/OnLowMemory`. |
| Safe area | `zigote_get_safe_area` → `MediaQueryData.Padding` → `SafeArea` widget insets by real device values, re-queried on resize/rotation. |
| wgpu-native | Upstream v29.0.1.1 ships all needed prebuilts; `build.zig.zon` now pins `wgpu_android_aarch64`, `wgpu_android_x86_64`, `wgpu_ios_aarch64`, `wgpu_ios_aarch64_simulator` (lazy), and `linkWgpuNative` selects them (Android = `linux` OS tag + `android` ABI; iOS simulator = `simulator` ABI). |
| Surface / backend | `createNativeSurface` handles iOS (CAMetalLayer, shared with macOS — both MetalView gates include `.ios`) and Android (`SDL_PROP_WINDOW_ANDROID_WINDOW_POINTER` → `SurfaceSourceAndroidNativeWindow`); instance backends: iOS = Metal-only, Android = Vulkan/GL. |
| MSBuild | `Zigote.Native.targets` maps `android-*`/`ios-*` RIDs → `-linux-android` / `-ios` triples. |
| DllImport | `NativeEngine.Lib` becomes `"__Internal"` under the `ZIGOTE_STATIC_NATIVE` define (iOS static linking). |

## The walls, in bring-up order (Gallery on device is the milestone — task list #7)

### 1. Native cross-compile — ✅ DONE for the iOS simulator (engine `fe237e3`)
`zig build {shared-lib,static-lib} -Dtarget=aarch64-ios-simulator -Doptimize=ReleaseFast
--sysroot "$(xcrun --sdk iphonesimulator --show-sdk-path)" --libc <file>` links. What it took:
- **`--libc` file** (include_dir/sys_include_dir = `$SDK/usr/include`) — zig bundles no iOS
  libc headers and `--sysroot` alone adds no include paths. The MSBuild targets must generate
  this file when the ios RID plumbing lands. (`--search-prefix` also propagates but lands as
  user includes → nullability warnings-as-errors in SDK headers. Don't.)
- `addAppleSdkPaths` (ex-addMacosSdkPaths) + the Xcode 26 **SubFrameworks** dir (UIKit's
  UIUtilities moved there); sdl3 binding translate-c gets the SDK include via its
  `sdl_system_include_path` option; miniaudio compiles as ObjC via a `.m` wrapper
  (AVAudioSession) + AVFoundation.
- Debug builds still hit zig std.debug's `_dyld_get_image_header_containing_address`
  (absent from the simulator tbd) — **use ReleaseFast/ReleaseSafe for iOS**.
- Device (`aarch64-ios`) build: same recipe with the `iphoneos` SDK — untested, expect minor
  deltas only. Android: still needs the NDK-sysroot equivalent of the same treatment.

### 2. Vendored SDL mobile drivers — ✅ DONE for iOS (Android still open)
The SDL package (now COMMITTED under `zig-pkg/` — gitignore whitelist — because it carries
local patches a fresh `zig fetch` would lose) has a full `.ios` platform: UIKit video,
CoreMotion sensor, MFI joystick, uikit power, coreaudio audio, dummy
haptic/process/dialog/tray, GL fully off (wgpu→Metal renders; the GLES render/video paths
statically reference gl*/EAGL symbols on iOS — keep `SDL_VIDEO_OPENGL_ES2` and
`SDL_VIDEO_RENDER_OGL_ES2` excluding ios). Android work remains: `.SDL_VIDEO_DRIVER_ANDROID`,
`src/core/android/` JNI glue, and the `SDLActivity` Java side in the app package.

### 3. iOS static lib + loop inversion (C# side) — NEXT UP
- ✅ `zig build static-lib` exists (35 MB ReleaseFast simulator archive verified); link it into
  the host binary with `ZIGOTE_STATIC_NATIVE` (DllImport "__Internal"). The `ios` .NET
  workload install was kicked off 2026-07-29 (check `dotnet workload list`).
- The C# `while (!ShouldQuit) Frame()` loops (ZigoteApp/Editor/Player) own the main thread;
  UIKit requires the system runloop to own it. SDL solves this with `SDL_RunApp`/
  `SDL_HINT_MAIN_CALLBACK_RATE` (its own UIApplicationMain + display-link drives callbacks) —
  the cleanest inversion is an `SDL_AppIterate`-style host entry that calls `App.Frame()` per
  tick. `Frame()` is already one-shot re-entrant; `WaitEvents` must be skipped on mobile.
- iOS lifecycle events arrive via SDL *event watches* at the transition moment (the poll loop
  may not run again before suspension) — extend the existing `resizeEventWatch` to flush/stop
  GPU work on `will_enter_background` directly (the polled `EVT_APP_BACKGROUND` copy is fine
  for Android/desktop).

### 4. Android packaging
`net10.0-android` TFM (workload available, not installed) or a thin Java `SDLActivity` +
Mono/NativeAOT embedding. Surface dies on background — recreate the wgpu surface on
`EVT_APP_FOREGROUND` (note in `createNativeSurface`). The `x86_64` wgpu archive covers the
installed API-34 emulator image.

### 5. Runtime hazards to fix when they become load-bearing (audit 2026-07-29)
Blockers for shipping, roughly ordered; file:line refs from the audit agent:
- Reflection late-bind of DevTools (`ZigoteApp.cs` ~`TryAutoInstallDevTools`, `PlayerMain.cs`)
  — works under `TrimmerRootAssembly` but needs `[DynamicDependency]` or a compile-time seam
  for mobile NativeAOT; `Aot.targets` sets `SuppressTrimAnalysisWarnings=true` (turn OFF to
  see the real problems) and macOS-only Homebrew `LinkerArg`s (must be conditioned).
- `EcsWorld` non-generic `Marshal.SizeOf/PtrToStructure` = `RequiresDynamicCode` (editor
  path mostly; Player exports use static registration already).
- `SaveStore` reflection-JSON default overloads (typed `JsonTypeInfo` overloads exist).
- `GalleryLocales.PanUnicodeFace` hardcodes `/System/Library/Fonts/...` (macOS-only path).
- `FileBrowserPlaces`/`FileOperations.Process.Start`/`NativeMenu`/`HttpListener` need
  platform guards; fonts must land in the mobile bundle (`Zigote.Fonts.targets` doesn't).
- Soft keyboard: `StartTextInput` already shows it on mobile SDL; keyboard geometry →
  `MediaQueryData.ViewInsets` is unwired (SDL has no direct inset query — use
  `SDL_GetWindowSafeArea` deltas or platform hooks).

### 6. Game export (task #9)
`ExportDialog` RID list + `GameExporter` staging know nothing of mobile. After Gallery works:
add `ios-arm64`/`android-arm64` RIDs, static-lib + `.app`/IPA staging for iOS, APK packaging
for Android, and a mobile `Zigote.Player` entry using the inverted loop. `PlayerMain`'s
content-dir probe needs an iOS-bundle candidate.

## Order of attack (agreed with the user)
1. Gallery on Android emulator + iOS simulator (validate by hand — user does this).
2. FSharp.Gallery, then Editor.
3. Player/Runtime/game export mobile targets.
