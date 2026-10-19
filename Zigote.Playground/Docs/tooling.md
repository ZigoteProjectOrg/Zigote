# Tooling

## Hot reload

Run under `dotnet watch` (or use Rider or Visual Studio "apply changes") and edits to `Build` methods land in the running app with every widget's state intact. Zigote hooks .NET's `MetadataUpdateHandler`, marks the affected widgets for rebuild, and keeps the instances.

## DevTools

Reference `Zigote.UI.DevTools` and press **Shift+D**:

- **Inspector.** Pick a widget on screen; see the live tree, bounds, box model, constraints and properties, and how many times each `Watch` rebuilt.
- **Performance.** A rolling frame-time chart and the hottest profiler scopes.
- **Reactive.** Signal writes, recomputes and `Watch` rebuilds per second, plus *which* computed or effect is churning. The number to check is the idle one: anything ticking while nothing on screen changes is a bug.

## The inspect protocol

Set `ZIGOTE_INSPECT=1` and the app opens a loopback socket with one-word verbs: `widgets`, `semantics`, `props`, `shot`, `stats`, `input`, `theme`, `locale`, `preview`, `window`. It can read the live trees, capture frames and inject real input. Two clients ship:

- **The Rider plugin.** Live previews of any `[Preview]` widget, with device sizes, themes and locales.
- **The MCP server** (`Zigote.Mcp`). Lets AI agents launch an app, look at it, tap, type and scroll.

## Previews

Mark a widget with `[Preview]` and the IDE renders it on its own, without navigating the app to reach it. A preview that throws shows its error and recovers on the next save.

## Environment switches

| Variable | Effect |
|---|---|
| `ZIGOTE_INSPECT=1` | Open the inspect socket |
| `ZIGOTE_DEBUG_DAMAGE=1` | Flash repainted regions |
| `ZIGOTE_CONTINUOUS=1` | Render every frame, even when idle (for meaningful frame timings) |
| `ZIGOTE_PLAYGROUND_PAGE=Layout` | Open this app on a given page |

## Headless tests

`Zigote.UI` depends on nothing above the GPU, so the widget layer runs without a window: build a tree, lay it out, dispatch synthetic input, and assert on the paint commands it emits. Every test in `Zigote.Tests` works this way. This playground follows the same idea: `dotnet run --project Zigote.Playground -- --self-test` compiles and constructs every example on every page, so a docs page can't silently drift from the API.
