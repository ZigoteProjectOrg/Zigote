# Changelog

All notable changes to the Zigote SDK are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

Every package in a release — `Zigote.UI`, `Zigote.UI.Adwaita`, `Zigote.Cli`,
`Zigote.Engine.runtime.<rid>` and the rest — carries the same version, the one in
`build/version.txt`. The native engine is released from
[Zigote.Engine](https://github.com/ZigoteProjectOrg/Zigote.Engine) under that same number, and each
entry below names the engine ABI it pairs with. The managed packages and the engine package of one
version are two halves of one ABI; mixing versions fails at startup by design (see
[`docs/sdk.md`](docs/sdk.md)).

## [Unreleased]

## [0.1.0] - 2026-11-04

First public release. Pairs with Zigote.Engine 0.1.0, ABI version 100.

### UI framework (`Zigote.UI`)
- A retained-mode widget kernel: `Build` runs once and the tree is kept, so hover, focus, caret,
  scroll offset and in-flight animations live on the widget instances. Layout, painting, input,
  focus, animation, navigation and text editing are all managed code over a small C ABI; nothing is
  delegated to the platform toolkit.
- Layout: `Column`, `Row`, `Stack`, `Wrap`, `Expanded`, `Padding`, `Align`, `SizedBox`,
  `ScrollView` with `ScrollsWith`, virtualized `ListView` and `GridView`, `InteractiveViewer`.
- Controls: `Label`, `Button`, `Card`, `Dialog`, `Snackbar`, `Tooltip`, `ContextMenu`, `Popover`,
  `RichText`, `SelectableText`, `Pressable`.
- Text: engine-backed shaping and measurement, selection, undo, and the `ITextInputClient` IME seam.
- Liquid Glass surfaces (`LiquidGlass`, `LiquidGlassLayer`, blend groups, `GlassGlow`), adaptive
  glass and the system glass preference.
- Animation: explicit transitions, implicit `AnimatedOpacity` / `AnimatedAlign` /
  `AnimatedSwitcher`, the fluent `widget.Animate()` API, an interruptible spring `Motion`,
  shared-element hero transitions and theme dissolves.
- Navigator 2.0: imperative `Push` / `Pop`, named routes, or a declarative page stack from a signal.
- Multiple OS windows (Wayland included), overlays, in-app and OS drag and drop, and one `AppMenu`
  model that becomes a native `NSMenu` on macOS and an in-window menu bar elsewhere.
- `ThemeData` plus `Spacing`, `Typography`, `Radii` and `Elevation` token scales; Tab / arrow / Esc
  focus traversal with modal traps; a platform-neutral semantics tree.
- Hot reload under `dotnet watch` on the desktop and on an attached Android device; transient UI
  state (scroll offsets, hover, focus) is carried across rebuilds automatically.
- Damage-driven rendering: layout and discrete-input damage derived from the paint-list diff, an
  idle frame loop that sleeps, edge-only repaints for carets, snackbars and secondary windows.

### Reactive core (`Zigote.Core`)
- `Signal<T>`, `Computed<T>`, `Effect` (`Inline` or `Deferred` to the frame loop), `Trigger`,
  `LinkedSignal<T>`, `Reactive.Batch`, and `Watch` as the bridge into the widget tree. Each `Watch`
  runs as a single effect; rebuild counts are live DevTools counters and
  `Reactive.TrackReactions` names the hottest reaction.
- `Background`: results delivered against a per-frame budget, `Slice` for work spread over frames,
  `Latest` for debounced latest-wins, failures reported instead of swallowed.
- Platform channels and the plugin contract, the generated native bindings into `libzigote`, the
  scene graph, assets and diagnostics.

### Adwaita design system (`Zigote.UI.Adwaita`)
- 148 `Adw*` types tracking libadwaita 1.10: both appearances, the nine accents, boxed-list rows,
  adaptive navigation and client-side decorations drawn by the app, including the macOS 27 traffic
  lights. Nothing links against GTK, so an Adwaita app runs unchanged on macOS and Windows.
- Form controls and editors: `AdwEntry`, `AdwTextField` with full IME composition,
  `AdwCheckButton`, `AdwSwitch`, `AdwSlider`, `AdwDropDown`, `AdwTabBar`, `AdwChip`, `AdwPaned`,
  `AdwTreeView<T>`, `AdwReorderableList`, `AdwColorPicker`, `AdwCurveEditor`, date and time
  pickers, a data table, pull to refresh, and `AdwCodeEditor` with multi-cursor editing, markers,
  completion and parameter hints.
- Adwaita is the only design system in this release; `Zigote.UI.Material` and the old
  `Zigote.UI.Gallery` were removed before it. The gallery (`Zigote.UI.Adwaita.Gallery`, 51 pages)
  doubles as the spec and has a headless `--self-test`.

### Libraries around the kernel
- `Zigote.UI.Charts`, `Zigote.UI.Localizations` (locales, plural rules, typed messages from ARB),
  `Zigote.UI.DevTools` (<kbd>Shift</kbd>+<kbd>D</kbd> overlay), `Zigote.UI.BottomSheet`,
  `Zigote.UI.Functional` (`View`), `Zigote.UI.FSharp` (`signal` / `computed` / `watch`),
  `Zigote.Modules.UI.CodeEditor`.
- `Zigote.Bloc`, `Zigote.Logging`, `Zigote.Preferences`, `Zigote.Persistence` (+ `.SQLite`),
  `Zigote.Reactive.R3`, `Zigote.Http` (+ `.FSharp`, with source-generated typed clients),
  `Zigote.Network`.
- `Zigote.Audioplayer` (queue, transport, gapless advance, equalizer, testable against an in-memory
  fake) and `Zigote.Videoplayer` (ffmpeg-driven playback, on-video controls in liquid glass,
  picture-in-picture, a media-session plugin). A wake-lock plugin keeps the screen awake.

### Tooling
- `Zigote.Cli` (`dotnet tool install -g Zigote.Cli`): `zigote create`, `zigote add android|ios`,
  `zigote run` (attached device or simulator if there is one, else this machine; `--widget`,
  `--inspect`), `zigote build apk|aab|ipa|exe|app|appimage` with icons, one version and signing,
  `zigote test --update-goldens`, `zigote logs`, `zigote doctor --fix`.
- `Zigote.Mcp`: an MCP server over stdio through which an LLM agent launches, drives, screenshots,
  measures, builds and audits a running app over the inspect protocol.
- `Zigote.Playground`: the documentation as an app — 32 pages with live examples compiled
  in-process, a widget catalog, a live editor and a `--self-test`.
- The Rider plugin (`tools/rider`): colour swatches, widget preview, widget and semantics trees.
- Profiling: the in-app profiler, `stats` / `profile` remote tools, the `Zigote-Engine`
  EventSource, and the zero-allocation hot-path test guards.

### Games and 3D
- A forward+ EEVEE-style renderer (cascaded shadows, SSAO/GTAO, SSR, bloom, TAA, AgX) over a
  backend-agnostic render graph, in the native engine.
- `Zigote.Runtime`, `Zigote.Game` (fixed-timestep loop), `Zigote.Scripting` (component lifecycle,
  C# hot reload), `Zigote.ECS` / `Zigote.World` / `Zigote.Save` over flecs, `Zigote.Physics2D`,
  `Zigote.Render2D`, `Zigote.Vfx`, `Zigote.Cinematics`, and the `Zigote.Graphs.*` node graphs that
  compile to WGSL.
- `Zigote.Editor` with hierarchy, inspector, asset browser, viewport, timeline, a graph editor, a
  command palette and a docked code editor; glTF materials import as authored. `Zigote.Player`
  runs an exported bundle, JIT or AOT.

### SDK and packaging
- One version for everything, from `build/version.txt`. `build/pack.sh` packs the managed closure,
  one `Zigote.Engine.runtime.<rid>` per RID, `Zigote.Android.Support` and `Zigote.iOS.Support`;
  a generated app references the SDK by version, so a phone build needs no Zig toolchain, NDK or
  Android sysroot.
- Provenance on every artifact: the commit in `InformationalVersion` and the `.nuspec`, SourceLink,
  deterministic builds, a `zigote-engine.json` manifest with a SHA-256 per native file, a
  `SHA256SUMS` per release, and author signing before any push.
- `build/publish.sh` produces self-contained per-RID bundles; the Linux ones can be built inside
  `build/Dockerfile.linux` for a glibc 2.36 floor. `zigote build appimage --aot` ships a gallery as
  one native binary.
- The release workflow builds the editor bundles and the SDK packages on each desktop OS and
  attaches them to the tag's GitHub release.

### Platforms
- macOS (arm64, x64), Linux (x64, arm64) and Windows (x64) from one SDL3 + wgpu code path.
- iOS and Android in bring-up: touch, lifecycle, safe area and both native builds work; the
  gallery runs on the iOS simulator and the Android emulator.
- The browser, experimental: a Zigote UI app compiles for `browser-wasm`, with the engine linked
  into `dotnet.native.wasm` and rendering through WebGPU.

### Known limitations
- No accessibility bridge yet: the semantics tree and the `ISemanticsBridge` seam exist, but no
  AT-SPI, UIA or VoiceOver implementation does, so screen readers do not see a Zigote app.
- The browser target is single-threaded and has no video playback or network audio streams.
- Android and iOS build the lean engine (no Assimp import, no Jolt physics); Windows and Linux get
  less real-hardware time than macOS.
- No third-party widget ecosystem.

[Unreleased]: https://github.com/ZigoteProjectOrg/Zigote/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ZigoteProjectOrg/Zigote/releases/tag/v0.1.0
