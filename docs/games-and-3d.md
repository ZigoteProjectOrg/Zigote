# Games, 3D and the editor

The 3D renderer, the gameplay layer and the visual editor are a **separate stack** from the UI
framework. They are built *with* `Zigote.UI` — the editor is an ordinary Zigote app — but nothing in
`Zigote.UI` or `Zigote.Core` depends on them, and an app that only draws widgets links none of it.

If you came here for the widget framework, you want [`../Zigote.UI/README.md`](../Zigote.UI/README.md)
instead.

---

## The shape of it

```
Zigote.Editor            authoring: hierarchy, inspector, viewport, graphs, export
        ▼
Zigote.Scripting         your gameplay code — Component subclasses, hot-reloaded
Zigote.World / .Save     spawning, tags, spatial queries, save & load
Zigote.ECS / .Physics2D  flecs entities; 2D physics (3D physics is Jolt, in the engine)
Zigote.Vfx / .Graphs.*   particles; node graphs that compile to WGSL
        ▼
Zigote.Runtime           scenes, prefabs, animation, the frame loop a shipped game runs on
        ▼
Zigote.Core → libzigote  the C ABI into the Zig + wgpu engine
```

`Zigote.Player` is the standalone host that runs an exported bundle; `Zigote.Editor --export` and the
editor's export dialog both produce one.

---

## A project on disk

A game lives **outside** this repository, in its own directory:

```
MyGame/
  MyGame.zigoteproj        JSON manifest
  assets/
    main.scene             the startup scene
    prefabs/*.prefab       reusable node templates
  Scripts/Scripts.csproj   your gameplay code (optional)
```

The manifest is small and diffable (`Zigote.Runtime.Scene.ZigoteProject`):

| Field | Meaning |
| --- | --- |
| `Name` | Display name; also the exported executable name. |
| `AssetRoot` | Asset directory, relative to the manifest. Default `assets`. |
| `StartupScene` | Scene loaded on open and on launch. Default `assets/main.scene`. |
| `ScriptProject` | Optional path to your gameplay `.csproj`. The editor builds it on startup and rebuilds on change. |
| `RenderSettings` | A full `ZgRenderSettings3D` — environment, post-processing, shadows. Start from `RenderDefaults.Settings3D()`. Debug-only fields are deliberately not persisted. |
| `WindowWidth` / `WindowHeight` | Standalone player window size. The editor sizes its own. |
| `DevToolsEnabled` | Ship the <kbd>Shift</kbd>+<kbd>D</kbd> overlay with the exported game. Off by default. |

Open one:

```sh
dotnet run --project Zigote.Editor -- path/to/MyGame.zigoteproj
dotnet run --project Zigote.Editor                                # or start empty and use File ▸ Open
```

Prefabs and scenes are JSON written by the runtime serializer. **Generate them from a tool that
references `Zigote.Runtime`, or author them in the editor — do not hand-write them.**

---

## Writing gameplay

Gameplay code is a `Zigote.Scripting.Component` subclass, attached to a scene node in the inspector:

```csharp
using Zigote.Core.Math3D;
using Zigote.Scripting;

public sealed class Spinner : Component
{
    private float _yaw;

    protected override void OnUpdate(float dt)
    {
        _yaw += dt * 1.5f;
        Rotation = Quat.FromEuler(0f, _yaw, 0f);   // pitch, yaw, roll
    }
}
```

`Position` / `Rotation` / `Scale` are synced from the scene node before `OnUpdate` and written back
after, so mutating them moves the object. The other lifecycle hooks are `OnCreate`, `OnDestroy`,
`OnEnable`, `OnDisable`. An exception in a component is logged and disables that component — it does
not take down the editor.

**`OnUpdate` runs on a fixed 120 Hz tick, not the render frame.** `dt` is always the same constant; a
slow render frame runs several ticks back to back and a fast one may run none. Smooth the visual side
with `Time.InterpolationAlpha`.

Static facades give scripts the engine without any wiring:

| Facade | For |
| --- | --- |
| `Physics` | Jolt 3D bodies — `CreateBody`, `AddForceAtPoint`, `TryRaycast`. (No joints yet.) Scripts own their bodies and sync the transform from them each tick. |
| `World` | Spawn a `.prefab` by path, parent nodes, tag and query them. |
| `Input` / `Gamepad` | Keyboard, mouse, controller. Set `ZIGOTE_GAMEPAD=1` to enable SDL gamepad input. |
| `Hud` | `Hud.Root` is a widget tree — the whole UI framework, in-game. |
| `Camera` / `RenderView` / `Instancing` / `Sprites` / `DebugDraw` | Camera control, render targets, GPU instancing, 2D sprites, debug geometry. |
| `Audio` / `Music` · `Vfx` · `Scenes` · `Save` · `Ecs` · `Time` | The rest of the runtime. |

Conventions worth knowing up front:

- Euler order is **yaw ∘ pitch ∘ roll** — `Quat.FromEuler(pitch, yaw, roll)`, matching the native
  physics `eulerToQuat` exactly, so the same `(pitch, yaw, 0)` drives a visual node and a body.
- Forward is `(−sin yaw, 0, −cos yaw)`.
- Built-in mesh primitives: `#cube` (unit), `#sphere`, `#quad`, `#cylinder` (r = 0.5, h = 1, Y axis —
  meant for wheels).

Script assemblies hot-reload: the editor rebuilds `ScriptProject` on change and swaps the components
without restarting.

---

## The editor

`Zigote.Editor` is a Zigote app, so everything in the widget framework applies to it — including
<kbd>Shift</kbd>+<kbd>D</kbd> DevTools.

| Panel | What it does |
| --- | --- |
| **Hierarchy** | The scene tree: reparent, rename, duplicate, delete. |
| **Inspector** | Transform, mesh, material, camera, physics, scripts, prefab overrides, presets. |
| **Viewport** | 3D and 2D modes, gizmos, and play mode in place. |
| **Asset browser** | Models, textures, audio, prefabs, scenes, with previews. |
| **Timeline** | Animation tracks. |
| **Tile palette** | 2D tilemap authoring. |
| **Console** | Editor and game logging, stdout included. |
| **Shading / VFX graphs** | Node graphs for materials and particle systems. Materials compile to WGSL; VFX graphs compile live in the editor and are baked to JSON on export. |
| **Settings** | Render settings and editor preferences, persisted per project in SQLite. |

Environment hooks for hands-free verification (they combine):

| Variable | Effect |
| --- | --- |
| `ZIGOTE_AUTOPLAY=1` | Enter play mode automatically once scripts finish building. |
| `ZIGOTE_SHOT=/path/out.bmp` | Dump the native 3D framebuffer (no HUD or UI overlay) to a file. |
| `ZIGOTE_SHOT_FRAME=N` | Which frame to capture. Forces continuous rendering so the counter advances. |
| `ZIGOTE_GAMEPAD=1` | Enable SDL gamepad input. |

---

## Rendering

A forward+, EEVEE-style pipeline over a backend-agnostic render graph, all of it in the native
engine:

```
shadow → sky → geometry + MRT G-buffer → SSAO/GTAO + contact → SSR
       → bloom → AgX tonemap → TAA → UI → composite
```

Cascaded directional shadows with lazily allocated spot/point atlases, horizon-based AO with optional
single-bounce SSGI, ray-marched screen-space reflections, auto-exposure, DoF, and a 16-channel debug
view — every tunable carried in `ZgRenderSettings3D` and editable in the editor's render settings.
GPU instancing, frustum culling, LOD and mesh streaming sit in `Zigote.Runtime`.

Full detail: [`../Zigote.Engine/docs/rendering.md`](../Zigote.Engine/docs/rendering.md).

---

## Shipping a game

The exporter stages content (VFX graphs baked to JSON), generates a player project with static script
registration, publishes per RID — the Zig native library cross-compiles as part of the build — and
packages the result (`.app` on macOS, a plain folder elsewhere).

From the editor: **File ▸ Export**. Headless, for CI:

```sh
dotnet run --project Zigote.Editor -- --export MyGame.zigoteproj \
    --rids osx-arm64,win-x64,linux-x64 \
    --mode both \
    --out ./dist
```

`--mode jit` publishes self-contained, `aot` publishes Native AOT, `both` produces each. The result
runs on `Zigote.Player`, which loads the bundled `game.zigoteproj` and its content.

---

## Related

- [`architecture.md`](architecture.md) — how the whole solution layers, including this stack.
- [`assets.md`](assets.md) — asset pipeline and content bundling.
- [`../Zigote.Engine/docs/`](../Zigote.Engine/docs/README.md) — the native engine: rendering, FFI,
  subsystems, building.
