# Zigote.Core

The **foundation layer** of Zigote — the seam between the native Zig/wgpu backend and every C# layer above it. It owns
the P/Invoke surface, the paint-command and event ABIs, 3D math, animation, reactive state, and the diagnostics
registries. Everything else in the solution (`Zigote.UI`, `Zigote.Game`,
`Zigote.Scripting`, the editor, …) builds on `Zigote.Core`; it depends only on `Zigote.Engine` (the native library) and
the BCL.

`net10.0`, `unsafe`, nullable-enabled. The native backend is built and copied in as a pre-build step via
`build/Zigote.Native.targets`, and P/Invoke bindings are generated from the Zig `export fn`s by
`Zigote.Generators`.

## What's inside

| Area           | Contents                                                                                                                                                                                                                                              |
|----------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Engine/`      | `ZigoteEngine` — the high-level facade over the native engine (init, frame loop, paint submission, text/clipboard/menu/audio/gamepad/window FFI). `NativeWindow`, `MacMenu`, `MouseCursor`. **Never expose `NativeEngine` members — wrap them here.** |
| `Native/`      | `NativeEngine` (`internal unsafe partial` — `[DllImport]` bindings source-generated from the Zig exports), `ZgStructs` (FFI struct layouts pinned to Zig), `NativeMenu`                                                                                                                      |
| `Paint/`       | `PaintList` (the frame's tagged paint-op byte stream; clip/transform/opacity stacks, NaN validation, UTF-8 memoisation), `PaintCommandView` (decoded view for tests), `TextLayout`                                                                                               |
| `Events/`      | `InputEvent` hierarchy, `Key`/`KeyCode`, `KeyChord`/`Keymap`, `EventPool`                                                                                                                                                                             |
| `Math3D/`      | `Vec2/3/4`, `Mat4`, `Quat`, `Ray`, `Transform3D`, `Frustum`, `Tolerance` — all `readonly struct` with operator overloading, backed by `System.Numerics`                                                                                               |
| `Types/`       | Geometry + design primitives: `Offset`, `Size`, `Rect`, `Constraints`, `EdgeInsets`, `Duration`, `Color`/`Colors`/`MaterialColor`, `ColorTemperature`                                                                                                 |
| `Animation/`   | `AnimationController`, `Ticker`, `Curves`, `Tween` (`FloatTween`/`ColorTween`/`OffsetTween`/`SizeTween`)                                                                                                                                              |
| `State/`       | Reactive primitives: `Signal<T>`, `Trigger`, `Computed<T>`, `LinkedSignal<T>`, `Effect`, and the `Reactive` runtime (graph lock, batching, deferred drain)                                                                                            |
| `Threading/`   | `Background` (structured background work: `Run`, `Latest`, `Slice`, `Deliver.WhenIdle`), drained per frame by the app                                                                                                                                 |
| `Platform/`    | `PlatformChannel` (named app↔platform channels), `JsonChannel`/`JsonEvents` (typed), `PluginHost` + `IPlatformPlugin`                                                                                                                                 |
| `Licenses/`    | `LicenseRegistry` — runtime third-party notices                                                                                                                                                                                                       |
| `Assets/`      | `AssetId` (GUID-backed stable identity), `AssetRegistry`, `AssetManager`, `AssetHandle`, `IAssetLoader`/`FileBytesLoader`, streaming load states                                                                                                      |
| `Physics/`     | `PhysicsWorld` + `PhysicsBodySettings` (the C# side of the native Jolt bridge)                                                                                                                                                                        |
| `Rendering/`   | `RenderBackend`/`RendererCaps`, `RenderSettings`, `RendererAbiInfo` (ABI version guard)                                                                                                                                                               |
| `Diagnostics/` | Engine-neutral registries: `DebugLog`, `DebugCommands`, `DebugVariables`, `DebugProfiler`, `Profiler`                                                                                                                                                 |
| `Lod/`         | `LodMath`, `StreamingPolicy`                                                                                                                                                                                                                          |

## The engine loop

`ZigoteEngine` is the public entry point; the native `NativeEngine` P/Invokes are internal.

```csharp
using var engine = new ZigoteEngine();
engine.Initialize(960, 640, "My App");

while (!engine.ShouldQuit)
{
    foreach (var evt in engine.PollEvents())
        HandleEvent(evt);

    var paint = new PaintList();
    BuildUi(paint);

    engine.BeginFrame(deltaTime);
    engine.SubmitPaintCommands(paint);
    engine.RenderFrameV2();
}
```

Most apps never touch this directly — `UiApp`/`ZigoteApp` in `Zigote.UI` drive the loop for you. Drop to `ZigoteEngine`
when building a custom host. For the zero-GC steady-state frame, drain events with
`PollEventsInto(reusableBuffer)` rather than `PollEvents()`.

## FFI ground rules

The C#↔Zig boundary is the reason this project is `unsafe`. A few invariants that cost real debugging time when broken:

- **Bindings are generated, never hand-written.** `Zigote.Generators/ZigoteBindingGenerator` is a Roslyn source
  generator that reads every `export fn` in `Zigote.Engine/src/ffi/*.zig` (and the enums in `src/abi.zig`) on each
  build and emits `[DllImport]` entries into the `internal unsafe partial` `NativeEngine` class. (`[DllImport]` is
  deliberate: the SDK's `LibraryImport` generator cannot process another generator's output.) The public surface is
  `ZigoteEngine`; adding a binding is `export fn` → build → wrap it in `ZigoteEngine`.
- **FFI struct layouts must match Zig exactly.** The wire contract is `Zigote.Engine/src/abi.zig`: paint commands cross
  as a tagged stream (`ZgPaintOpHeader` + per-kind records) and `ZgEvent` is 44 B. Offsets are pinned by `comptime`
  asserts in Zig, and `AbiManifestTests` checks the C# mirrors against `zig build ffi-manifest`. A layout change means
  bumping `RendererAbiInfo.ExpectedAbiVersion` and the Zig `abi_version` (currently **100**, the V2 contract).
- **Native (Zig/shader) changes require a full process restart** to take effect — they apply at load, not via hot
  reload.

## Reactive state

`Signal<T>` = what is true now; a C# `event` = what happened (a one-shot fact). Never cross the two.

```csharp
var selected = new Signal<SceneNode?>(null);
using var sub = selected.Subscribe(n => label.Text = n?.Name ?? "Nothing selected");
selected.Value = node;                       // fires only if changed
selected.Set(node);                          // always fires

var hasSelection = Computed.From(selected, n => n is not null);
```

## Notes

- `Zigote.Core` knows nothing about widgets, scenes, or scripting — those live above it. It is usable standalone (2D
  paint + events + a window) as the thinnest possible Zigote host.
- The design-token *scales* (spacing, typography, radii) live in `Zigote.UI/Theme/`, not here; this project holds only
  the appearance-independent primitives (`Color`, `Rect`, `EdgeInsets`, …).
- See [docs/architecture.md](../docs/architecture.md) for how the solution layers, and
  [Zigote.Engine/docs/ffi-reference.md](../Zigote.Engine/docs/ffi-reference.md) for the FFI contract in full.
