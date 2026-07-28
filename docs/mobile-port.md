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

### 1. Native cross-compile (first, hard)
`zig build shared-lib -Dtarget=aarch64-ios --sysroot "$(xcrun --sdk iphoneos --show-sdk-path)"`
currently dies in the C deps: `AvailabilityMacros.h` not found (SDL translate-c), `sys/types.h`
(zlib under freetype/png). The `--sysroot` include paths don't propagate to dependency
sub-builds — `addMacosSdkPaths` in `Zigote.Engine/build.zig` does exactly this for macOS
cross-arch and needs generalizing to iOS (and NDK sysroot for Android:
`~/Library/Android/sdk/ndk/26.2.11394342/toolchains/llvm/prebuilt/darwin-x86_64/sysroot`).
Expect to touch each `b.dependency(...)` module the way `addMacosSdkPaths` is applied today.

### 2. Vendored SDL has no mobile video driver
`zig-pkg/sdl-0.4.0+3.4.0-*/build.zig` hardcodes `.SDL_VIDEO_DRIVER_UIKIT = false` (line ~434),
`.SDL_VIDEO_DRIVER_ANDROID = false` (~418), and its OS switch (~66–92) only knows
windows/linux/macos/emscripten. The upstream C sources for `src/video/uikit/` and
`src/video/android/` ARE present in the vendored tree — they're just never compiled. Work:
add `.ios`/android branches (sources + `UIKit`/`CoreMotion` frameworks resp. JNI glue
`src/core/android/`), flip the driver flags per-target. Android additionally needs SDL's Java
side (`SDLActivity`) in the app package.

### 3. iOS static lib + loop inversion (C# side)
- iOS forbids app-sandbox dylibs: add a `static-lib` step to `Zigote.Engine/build.zig`
  (`b.addLibrary(.linkage = .static)` reusing the same ffi module) and link it into the
  NativeAOT binary (`ZIGOTE_STATIC_NATIVE` + `PublishAot` + `-targetos ios`; .NET `ios`
  workload is available but NOT installed — `dotnet workload install ios`).
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
