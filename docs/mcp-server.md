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
| `launch` | `dotnet run` a project with `ZIGOTE_INSPECT=0`, wait for the announced port, remember it as the default target. `watch: true` runs it under `dotnet watch` instead — save a file and the UI hot-reloads in place, surviving even the port change a rude-edit restart causes. Optional `preview` (sets `ZIGOTE_PREVIEW`) and `hide_window`. |
| `stop` | Kill an app `launch` started — one by pid, or all of them. |
| `logs` | The last lines a launched app printed — build errors, log output, unhandled exceptions. Kept after the app exits, because a crash's output is read after the crash. |
| `screenshot` | The current frame as a PNG, plus its size in layout points. Taken at the end of a frame, so a shot after `tap` shows the tap's effect. `scale` shrinks it for token-frugal checks. |
| `find` | Search without dumping a tree: `label`/`role` match the semantics tree, `type` the widget tree. Matches come back with bounds and a tappable center point. |
| `tap_widget` | Find by semantic label and click the center — no coordinate math. Ambiguity is an error that lists the matches, resolvable with `role` or `index`. |
| `wait_for` | Poll until a label appears (or, with `gone`, disappears) — async dialogs, loading pages, clearing toasts. |
| `widget_tree` | The live widget tree: id, type, description, bounds, children. `max_depth` prunes big trees. |
| `semantics_tree` | The accessibility tree — roles, labels, values, actions. |
| `widget_props` | One widget's full property list, by the id `widget_tree` reported. |
| `tap` / `drag` / `scroll` | Synthetic pointer input through the app's real dispatch pipeline. Coordinates are layout points — the same space `screenshot` reports. |
| `type_text` / `press_key` | Text commit to the focused widget; physical keys with modifiers (`cmd+shift`). |
| `preview_targets` / `set_preview` | List the previewable widgets; swap the shown one without restarting. |
| `resize` | Lay the app out at a device size — `390x844` is a real phone layout, breakpoints and all. Omit the size to go back to the window's. |
| `set_theme` / `locales` / `set_locale` | Theme and locale switching. |
| `raw_command` | Escape hatch: one raw protocol line, e.g. `window hide`. |

Every mutating tool (`tap`, `tap_widget`, `drag`, `scroll`, `type_text`, `press_key`,
`set_preview`, `resize`, `set_theme`, `set_locale`) takes `screenshot: true` to return the
resulting frame in the same call — act and observe in one round trip.

One protocol verb has no typed tool yet: `stats` returns the frame/CPU/memory sample DevTools
keeps — `fps` (with min/max), `frame_ms`, `cpu_pct`, `mem_mb`, `gc_mb` and the paint-command
counts — as one JSON object, via `raw_command { command: "stats" }`. Drive the app, then ask what
it cost. The frame numbers only mean render pace under continuous rendering (`ZIGOTE_CONTINUOUS=1`);
an idle retained UI presents no frames, which is the point.

A typical agent session:

```
launch      { project: "Zigote.UI.HelloWorld", hide_window: true }
find        { role: "Button" }                        → Button 'Increment' at (376,476)
tap_widget  { label: "Increment", screenshot: true }  → tapped, and here is the new frame
wait_for    { label: "1", role: "Text" }              → the counter really moved
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
- **No streaming.** The protocol's `stream` command (pushed frames) is the Rider preview panel's
  channel; an agent works in request/response, so the MCP server only uses `shot`.
