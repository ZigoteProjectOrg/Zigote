# The MCP server — driving Zigote apps from an LLM agent

`Zigote.Mcp` is an [MCP](https://modelcontextprotocol.io) server over stdio. It gives coding
agents — Claude Code, or anything else that speaks MCP — the same view of a running Zigote app
that the Rider plugin has, as typed tools: launch an app, read its widget and semantics trees,
take screenshots, click, drag, type, switch theme, locale and device size.

It is a thin bridge, not a second inspector. Everything it does goes through the
[inspect protocol](../tools/rider/README.md) served by `Zigote.UI.Host.InspectServer` — the same
loopback socket and one-word commands a terminal reaches with
`echo widgets | nc 127.0.0.1 $port`. The MCP server adds three things on top: process management
(launch an app and find its port), typed tool schemas an agent can call without knowing the
protocol, and BMP→PNG conversion so screenshots come back as images the model can actually see.

## Setup

```sh
claude mcp add zigote -- dotnet run -v q --nologo --project /path/to/Zigote/Zigote.Mcp
```

That writes a `.mcp.json` in the directory the session starts in, which is git-ignored here — it is
per-checkout configuration, and the same file usually carries a machine's other servers (a Rider
port, for one) that mean nothing on anyone else's machine.

The `-v q --nologo` matters: stdout is the MCP protocol stream, and those flags are what keep
`dotnet run`'s own build chatter out of it.

## The tools

| Tool | What it does |
|---|---|
| `launch` | `dotnet run` a project with `ZIGOTE_INSPECT=0`, wait for the announced port, remember it as the default target. `watch: true` runs it under `dotnet watch` — save a file and the UI hot-reloads in place, surviving even the port change a rude-edit restart causes. Optional `preview` (sets `ZIGOTE_PREVIEW`), `hide_window`, and `env` for extra environment variables (`ZIGOTE_CONTINUOUS=1`, debug flags). |
| `stop` | Kill an app `launch` started — one by pid, or all of them. |
| `logs` | What a launched app printed — build errors, log output, unhandled exceptions. Kept after the app exits. `filter` is a case-insensitive regex, `new_only` returns only what was printed since the previous call, and runs of one repeated line are folded into `(×N)`. |
| `info` | The app's state in one line: layout size, theme, locale, focused and hovered widget, open overlays, widget count and tree depth, whether anything is animating. The cheap first look. |
| `screenshot` | The current frame as a PNG, plus its size in layout points. Waits for running animations to finish (up to 1 s), so the frame is the result and not the transition. `label` or `region` crops it — a smaller image costs fewer tokens; `scale` changes the density. |
| `find` | Search without dumping a tree: `label`/`role` match the semantics tree, `type` the widget tree (and returns widget ids). One line per match with its tappable center and size. |
| `tap_widget` | Find by semantic label and click the center — no coordinate math. Wheels an offscreen target into view first. Ambiguity is an error that lists the matches, resolvable with `role` or `index`. |
| `wait_for` | Poll until a label appears (or, with `gone`, disappears). Without a label, waits until animations have finished. |
| `semantics_tree` | The accessibility tree as an indented outline — `Role 'label' =value [state] @center`. Unlabelled groups are dropped; `visible_only` skips what is scrolled out. |
| `widget_tree` | The live widget tree as an indented outline. Single-child wrappers share a line (`A > B > C`) and bounds are printed only where they change. `root` scopes it to a subtree, `max_depth` prunes, `ids` adds the ids `widget_props` takes. |
| `widget_props` | One widget's full property list, by id. |
| `widget_at` | What a click at a point would land on, and the widgets around it — the answer to "why did my tap do nothing". |
| `tap` / `drag` / `scroll` | Synthetic pointer input through the app's real dispatch pipeline, in layout points. `tap` names the widget that took the click. `scroll` takes `dy` positive-is-down and says so when nothing moved. `drag` with `touch: true` drags a finger instead of the mouse — the gesture that scrolls and flings lists. |
| `type_text` / `press_key` | Text commit to the focused widget; physical keys with modifiers (`cmd+shift`). |
| `batch` | Several tools in one call, in order, stopping at the first failure: `steps: [{tool, args}, …]`. |
| `preview_targets` / `set_preview` | List the previewable widgets — with the name, device size and theme their `[Preview]` gave them, and the properties each takes; swap the shown one without restarting, optionally setting those properties (`My.App.Card?title=Espresso`). |
| `resize` | Lay the app out at a device size — `390x844` is a real phone layout, breakpoints and all. Omit the size to go back to the window's. By default only the layout changes; `window: true` resizes the OS window itself — framebuffer, swapchain and all. |
| `windows` | Lists the app's OS windows; `select: <id>` points the per-window tools (screenshot, trees, find, tap, type, scroll…) at a secondary window, `select: 0` back at the main one. On the wire this is an `@ID ` prefix on the command. |
| `set_theme` / `locales` / `set_locale` | Theme and locale switching. `set_theme` goes through the app's `SetDarkAppearance`, so a design system (Adwaita) or an app with its own appearance state switches *its* palette. |
| `stats` | Frame health in one line: `fps` (with min/max), `frame_ms`, `cpu_pct`, `mem_mb`, `gc_mb`, `alloc_kb_per_frame` (UI-thread alloc, ~1 s average — near zero on a healthy steady-state app), GC collection counts, jank frame counts and the paint-command counts. |
| `profile` | CPU-profile the app: capture N frames (default 120) of engine `Profiler` scopes, write a Chrome-Trace JSON (the path is in the reply) and return the hottest scopes as a per-frame average self/total ms table. |
| `raw_command` | Escape hatch: one raw protocol line, raw JSON reply — `window hide`, or `semantics` / `widgets` when a script wants the whole tree as JSON. |

Every mutating tool takes `screenshot: true` to return the resulting frame in the same call — act
and observe in one round trip — and `batch` folds a whole sequence into one.

Replies are shaped for a reader who pays per token: trees are outlines rather than JSON, matches
are one line each, and nothing prints a field that is null or implied. The order to reach for
things is `info`, then `find` / `semantics_tree`, then a cropped `screenshot`, and a full-frame
screenshot only when the whole picture matters.

The frame numbers only mean render pace under continuous rendering (`ZIGOTE_CONTINUOUS=1`) or
while something animates; an idle retained UI presents no frames, which is the point.

A typical agent session:

```
launch      { project: "Zigote.UI.HelloWorld", hide_window: true }
find        { role: "Button" }                        → [0] Button 'Increment' @376,476 56x56
batch       { steps: [ { tool: "tap_widget", args: { label: "Increment" } },
                       { tool: "wait_for",   args: { label: "1", role: "Text" } },
                       { tool: "screenshot", args: { label: "1" } } ] }
```

And the UI-development loop, where the agent is editing the app it is looking at:

```
launch      { project: ".", watch: true, hide_window: true }
screenshot                        → see the current state
…edit source files, save…         → dotnet watch hot-reloads in place
wait_for    { label: "…" }        → the change is live
screenshot                        → confirm visually
```

## Talking to an app you started yourself

`launch` is a convenience, not a requirement. Start any Zigote app with `ZIGOTE_INSPECT=0`, read
the line it prints —

```
zigote inspect: 127.0.0.1:55752
```

— and pass that port as the `port` argument to any tool. This is how an agent attaches to an app
that is already running under a debugger, or one built with flags `launch` does not know about.

## Boundaries worth knowing

- **Loopback only.** The inspect socket exposes the app's entire UI state and accepts synthetic
  input, so `InspectServer` binds `127.0.0.1` and nothing else. The MCP server inherits that: it
  can only drive apps on the same machine.
- **Cold builds take as long as they take.** `launch` waits `wait_seconds` (default 120) for the
  port to appear; a first build that drags the native engine in can need more. The failure
  message carries the app's recent output, so a compile error reads as itself rather than as a
  timeout — and `logs` has the rest.
- **`resize` is layout unless you ask for the window.** By default it sets the size the tree is
  measured at and the OS window stays put. `window: true` is a real OS resize — the one that
  reaches bugs only a framebuffer change triggers; the window manager may clamp it, and the
  reply reports the size that resulted. (`env: { "ZIGOTE_RESIZE_TORTURE": "1" }` on `launch` is
  the continuous-sawtooth version.)
- **A mouse drag does not scroll.** On the desktop only the wheel and the scrollbar move a list,
  exactly as for a person; `scroll`, or `drag` with `touch: true`, is the way.
- **Virtualized rows exist only once scrolled to.** `find` and `tap_widget` cannot see a row a
  list has not built yet — `scroll` first.
- **No streaming.** The protocol's `stream` command (pushed frames) is the Rider preview panel's
  channel; an agent works in request/response, so the MCP server only uses `shot`.
