# Zigote.UI.DevTools

The in-app debug overlay: **Shift+D** in any host that has it installed. It is built from ordinary
`Zigote.UI` widgets and `Zigote.UI.Charts`, so it renders inside the app it is inspecting, with no external tooling.

`Zigote.UI` has no compile-time knowledge of this package — installing it is the host's opt-in.

## Install

Referencing the project is usually all you need: the host auto-installs the overlay with the `Auto`
profile (see `ZigoteApp.TryAutoInstallDevTools`).

```csharp
protected override void OnInit()
{
    base.OnInit();
    DevTools.Install(App!, DevToolsProfile.TwoD);   // explicit form: pins the profile
}
```

| Profile          | Tabs                                                                                            |
|------------------|-------------------------------------------------------------------------------------------------|
| `TwoD`           | General + 2D·UI — a pure UI/2D app has no renderer pipeline to inspect                          |
| `ThreeD`         | General + 2D·UI + 3D·Render                                                                     |
| `Auto` (default) | 3D once the renderer has drawn a 3D frame, otherwise 2D; re-evaluated each time the panel opens |

`ZigoteApp.AutoInstallDevTools = false` (static) suppresses the automatic install. `DevTools.Register(panel)`
adds a host-specific panel (scene, physics, gameplay) to the current overlay.

## Panels

**General** — Overview, Profiler, Memory, GPU, Reactive, Logs, Console, Variables. **2D · UI** — UI Inspector (widget
tree, layout bounds, repaint rainbow), UI Paint (paint-command stats), Semantics. **3D · Render** — Pipeline, Renderer.

## The numbers to watch

Live counters, also readable from the console (`get ui.watch_rebuilds`):

| Variable            | Means                                         |
|---------------------|-----------------------------------------------|
| `ui.watch_rebuilds` | `Watch` subtree swaps, excluding first build  |
| `reactive.runs`     | computed recomputes + effect runs             |
| `reactive.writes`   | committed signal writes                       |
| `reactive.deferred` | cross-thread effect backlog at the last drain |

The diagnostic that matters: with the app **idle and nothing on screen changing**, these should be flat. A climbing
`runs` or `watch_rebuilds` is reactive churn — something is recomputing for nothing. Conversely, one interaction should
move `watch_rebuilds` by the number of subtrees that genuinely changed (in `Zigote.UI.HelloWorld`, exactly one per
button press).

## Console commands

Registered under `app`: `menu`, `compact`, `popout` (devtools in their own OS window), `fullscreen`,
`gc`, `profile [frames]` (writes `profile_capture.json`; also on F7), `quit`.

## Environment variables

| Variable                  | Effect                                                               |
|---------------------------|----------------------------------------------------------------------|
| `ZIGOTE_DEVTOOLS_OPEN=1`  | open the panel on boot — no keypress, for screenshots and smoke runs |
| `ZIGOTE_DEVTOOLS_CYCLE=1` | open and auto-advance through every tab                              |

## See also

- [`docs/notes/devtools-widget-tree.md`](../docs/notes/devtools-widget-tree.md) — design of the widget-tree inspector (virtualised
  rows, depth guidelines, the Selected section).
- [`Zigote.UI.HelloWorld`](../Zigote.UI.HelloWorld/README.md) — smallest host that installs it.
