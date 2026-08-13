# Zigote for Rider

Things Rider does not know how to do for a framework it has never heard of: show what a colour literal
looks like, run one widget on its own, and show you the widget tree behind what you are looking at.

```sh
cd tools/rider
./gradlew buildPlugin      # → build/distributions/zigote-rider-0.1.0.zip
./gradlew runIde           # a sandbox Rider with the plugin loaded
./gradlew test             # the parsing, without an IDE
```

Install the zip through **Settings → Plugins → ⚙ → Install Plugin from Disk**. Set `riderVersion` in
`gradle.properties` to the Rider you actually run — the first build downloads that Rider (~2 GB).

**No JDK or Gradle has to be installed first.** The wrapper is committed and pinned to Gradle 8.14.3 —
not 9.x, where the IntelliJ Platform plugin's `instrumentCode` dies in ASM — and 8.14 in turn does not
run on a JDK 25, which it reports as a bare `* What went wrong: 25.0.4`. So the daemon JVM is pinned
to 21 in `gradle/gradle-daemon-jvm.properties` and downloaded if it is missing, whatever the JDK on
PATH happens to be. The Java 21 *toolchain* the platform compiles against is downloaded by the foojay
resolver in `settings.gradle.kts`, which is a separate thing wanting the same version.

---

## Colours

A gutter swatch next to every colour literal, and the platform colour picker when you click it. The
edit is written back in the shape it was found in — a `Color.Rgb` stays a `Color.Rgb`.

| Written as | Read as |
| --- | --- |
| `new Color(0xFF2196F3)`, `Color.FromHex(0x802196F3)` | `0xAARRGGBB`; six digits or fewer are opaque |
| `new Color(0.5f, 0.25f, 0f)`, `new Color(r, g, b, a)` | 0..1 floats |
| `Color.Rgb(16, 18, 22)` | 0..255, opaque |
| `Color.Rgba(16, 18, 22, 0.5f)` | 0..255 channels, 0..1 alpha |

Matched by pattern, not by resolving the symbol. Rider keeps the C# PSI in the ReSharper backend, so
recognising the *type* would mean a second plugin half built against the ReSharper SDK — for a feature
whose whole job is "show me what that number looks like", which needs no type information to be right.
The cost is that a `Color` from some other library written the same way also gets a swatch.

## Starting a preview

1. **View → Tool Windows → Zigote** (right edge), **Preview** tab.
2. Press **Run app**. With a `.cs` file open it runs that file's project; otherwise it offers the
   projects in the solution.
3. When the status reads `port NNNNN`, pick a widget from **Widget:**. Choosing another one swaps the
   app to it without restarting.

Or from the editor: caret inside a widget class, <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd>.

Already running it yourself? `ZIGOTE_INSPECT=41337 dotnet run --project …`, then **Attach…** with `41337`.

## The Zigote tool window

Three tabs, all views of one running app.

| Tab | What it shows |
| --- | --- |
| **Preview** | The app's frame, drawn in the tab — and **live**: click, drag, scroll and type on the picture and it happens in the app (click the picture first so it has keyboard focus). Pick a project, a widget, a device (and **Landscape** to rotate it), a theme, a locale; `Zoom` is Fit / 100% / 200%. **Live** streams frames at animation rate as they change; off, the picture refreshes after each click instead. The **Locale** combo appears only when the app has a `LocalizationsScope`, and a narrow panel collapses the toolbar to its essentials (see **Compact toolbar**). |
| **Widgets** | The live widget tree. Select a node to outline it on the frame and read its properties; the filter keeps a 300-node tree navigable. |
| **Semantics** | The accessibility tree the app would hand a screen reader — role, label, actions, size. |

### Compact toolbar

The full toolbar needs ~1600 points to fit on one row, so in a tool window docked to the right edge it
wraps into five — 127 px taken from the picture in a 420-point panel. Compact keeps what a preview is
*used* through (**Run app**, **Stop**, **Refresh**, **Live**, the widget and the device) and gets that
down to two rows: captions become tooltips, the verbs become icons, and the widget name drops its
namespace (`ImageGridPage`, with the whole name still on hover). Wider than that it is a single row.

It follows the panel's width on its own below 700 points; the chevron at the start of the toolbar
overrides it in either direction, and from then on the width stops deciding — a mode that undoes what
you just clicked is worse than no mode. Nothing moves into a menu: expanding puts every control back
where it was.

### Density

The frame is captured at the density it is about to be drawn at — on a Retina MacBook that is two
pixels per point, so the picture lands on the screen 1:1 instead of being a half-resolution image the
compositor enlarges. That is the difference between preview text that looks soft next to the same app
in its own window and text that looks identical to it.

It is *what is drawn*, not what the display could do, so the bill matches the picture: shrunk to half
size under **Fit** — a docked panel showing a desktop-sized app — it costs exactly what 1× cost. Full
size at 100% on a Retina panel is four times the pixels, and a capture is roughly linear in them
(~24 ms at 1×, ~88 ms at 2× for a 1240×820 app here), which **Live** spends as frame rate. Shrink the
panel, pick a phone device, or turn Live off for the still that costs nothing to be sharp.

### Devices

**Device** does not draw a phone-shaped frame around a desktop layout — it tells the app to lay its
**live tree** out at that size, so `MediaQuery`, breakpoints and wrapping behave as they would on the
device. Presets are logical points (a Pixel 8 is 412×915 points, not 1080×2400 pixels); getting that
wrong is the one mistake a device preview exists to catch.

- **Panel (adapt)** — the default while developing: the app follows the tool window as you resize it.
- **App window** — hand layout back to the app's own window size.
- Phones, tablets and desktop sizes for checking a specific one.

In preview mode the app's own window is **hidden** — two windows showing the same thing, one of them
laid out for a phone, is worse than none. On macOS it leaves the Dock and the ⌘-Tab switcher too:
hiding a window there still leaves a foreground app, so the preview used to sit in the Dock as an
icon that brought back nothing. `ZIGOTE_PREVIEW_WINDOW=show` keeps both.

### Hot reload

Off by default, and that is a Linux problem rather than a preference: `dotnet watch` opens a file
watcher per directory, and next to a running Rider that reliably exhausts the inotify **instance**
limit — the app starts, prints its port, and watch kills it a second later. Tick **Hot reload** to run
under `dotnet watch` anyway (raise `fs.inotify.max_user_instances` first if it exits immediately);
leave it off and use **Refresh**, which costs a frame rather than a restart. The limit is inotify's,
so on macOS — where `dotnet watch` watches through FSEvents — the tick is the default worth having.

Two things the plugin does to make watch actually usable inside Rider:

- **Edits are saved for you.** `dotnet watch` reloads on file *save*, and Rider only autosaves on
  window deactivation — which never happens while you edit code and glance at a tool window in the
  same Rider window. While a watch session is live, the plugin saves documents after ~0.7 s of typing
  pause; without that, hot reload "does not work" while working perfectly from a terminal.
- **Restarts keep your setup.** A rude edit (new field, changed constructor) makes watch restart the
  process, which comes back with a new socket and its defaults. The panel reconnects and pushes back
  the previewed widget, device size, theme and locale it had.

**Run app** starts the project owning the file you have open — pressing it again replaces the running
app rather than racing it, and **Stop** puts it down. **Attach…** points the panels at an app
you started yourself — `ZIGOTE_INSPECT=41337 dotnet run …`, then give it `41337`. **Preview Zigote Widget**
(<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd>, or the editor context menu) starts it pointed at the
widget your caret is in. Either way it runs under `dotnet watch`, so saving reloads the widget in
place — Zigote's existing hot-reload bridge, not something this plugin adds.

## None of that is in this plugin

The previewer is `Zigote.UI.Host.WidgetPreview` and the panels read
`Zigote.UI.Host.InspectServer`, both in the framework. The whole contract is two environment variables
and a loopback socket:

```sh
ZIGOTE_PREVIEW=My.App.SettingsPage dotnet watch run --project My.App   # show one widget
ZIGOTE_PREVIEW_LIST=1              dotnet run       --project My.App   # list what there is to show
ZIGOTE_INSPECT=0                   dotnet run       --project My.App   # → zigote inspect: 127.0.0.1:41337
zigote preview My.App.SettingsPage                                     # the same, from the CLI
```

A preview target is a widget type with a parameterless constructor, or a static parameterless method
returning a `Widget`.

The socket takes one command per connection and answers with one line of JSON. `ZIGOTE_INSPECT=0` picks
a free port and prints it; loopback only, and off unless asked for.

| Command | Reply |
| --- | --- |
| `widgets` | `{"tree":{"id","type","desc","x","y","w","h","children":[…]}}` |
| `semantics` | `{"tree":{"id","role","label","value","hint","flags","actions","x","y","w","h","children":[…]}}` |
| `targets` | `{"targets":["My.App.SettingsPage", …]}` |
| `preview <Type>` | `{"ok":true}` — swaps the shown widget live |
| `shot [scale]` | `{"format":"bmp","w","h","scale","data":"<base64>"}` — `w`/`h` are layout points, the picture is that × `scale` pixels |
| `size WxH` / `size window` | `{"ok":true,"w","h"}` — lay the live tree out at a device size |
| `theme dark\|light` | `{"ok":true,"w","h"}` |
| `locales` | `{"current":"en","locales":["en","es","ar"]}` — empty when the app has no `LocalizationsScope` |
| `locale <tag>` | `{"ok":true,"w","h"}` — switch the app's locale live |
| `input down\|up X Y [left\|right\|middle]` | `{"ok":true}` — synthetic press/release at layout coordinates |
| `input move X Y` / `input scroll X Y DX DY` | `{"ok":true}` — pointer move / wheel ticks |
| `input keydown\|keyup NAME [shift+ctrl+alt+cmd]` | `{"ok":true}` — a physical key, by `KeyCode` name |
| `input text …` | `{"ok":true}` — commit text to the focused widget, verbatim |
| `stream [scale] [fps]` | one JSON header line (`{"format","stream","scale"}` — the density every frame is at, since a raw BMP has nowhere to carry it), then each changed frame as 4-byte big-endian length + BMP, pushed until the client hangs up |
| `window hide\|show` | `{"ok":true}` |
| `props ID` | `{"id","type","props":{…},"x","y","w","h"}` |
| `stats` | `{"fps","fps_min","fps_max","frame_ms","cpu_pct","mem_mb","gc_mb","ui_paint_commands","overlay_paint_commands"}` — the sample DevTools keeps; frame pace is only meaningful under `ZIGOTE_CONTINUOUS=1` |

Injected input takes the exact pipeline OS input does — hit-testing, focus, shortcuts — so a scripted
click is a real click. `stream` sends nothing while the picture is unchanged — and does no capture
work at all while nothing repaints, so a stream attached to an idle app costs it nothing. A blocking
read doubles as "wait for the next repaint". Captures composite the overlay layer and are taken at
the *end* of a frame — the dialog or menu a click just opened is in the very next picture.

Anything unparseable comes back as `{"error":"…"}`. So the same three views are available to a VS Code
extension, a script, or a shell:

```sh
echo widgets | nc 127.0.0.1 41337 | jq '.tree.children[0].type'
```

LLM agents get the same protocol as typed MCP tools — launch, screenshot, tap, tree — through
[`Zigote.Mcp`](../../docs/mcp-server.md).

---

## When it does not work

**The app starts and closes immediately.** Look at the run console. `No available video device` or
`XDG_RUNTIME_DIR is invalid or not set` means the process did not inherit the desktop session — the
plugin forwards `DISPLAY`, `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR`, `XDG_SESSION_TYPE`, `XAUTHORITY` and
`DBUS_SESSION_BUS_ADDRESS` from the IDE for exactly this reason, so if you still see it, launch Rider
from a normal desktop session rather than a stripped shell.

**The widget dropdown is empty.** It fills from the running app, so the status next to the buttons is
the thing to read — it says which of these is happening:

| Status | Meaning |
| --- | --- |
| `no app running — press Run app` | nothing was started |
| `starting… (the first build can take a while)` | building; a cold checkout builds the Zig engine too |
| `port NNNNN` | connected — press **Refresh** if the list is still stale |
| `app exited (code N) before it was ready` | it died during startup; the run console says why |
| `nothing answered on port NNNNN` | the app runs but did not open the socket — `ZIGOTE_INSPECT` did not reach it |

**Refresh** re-reads the list, which is what you want when the tool window was opened after the app was
already up. **Attach…** bypasses launching entirely, which separates "the plugin cannot start an app"
from "the plugin cannot talk to one". Every failure is also logged to `idea.log` with a `zigote:` prefix.

To check the app half without the IDE at all:

```sh
ZIGOTE_INSPECT=41337 dotnet run --project Zigote.UI.Adwaita.Gallery &
until nc -z 127.0.0.1 41337; do sleep 1; done   # it has to build and open a window first
echo targets | nc 127.0.0.1 41337
```

The wait is not optional — querying immediately gets `Connection refused`, because the app is still
building. That is also why the tool window waits rather than reporting "no app" a second after you
press **Run app**.

**`inotify instance limit reached`.** `dotnet watch` ran out of file watchers, usually because several
are already running. Close the other ones, or raise `fs.inotify.max_user_instances`.

## Not here yet

- **Swatches on named colours** (`Colors.Blue`, `AdwPalette.Accent`). Those need the palette's values,
  which means either duplicating them here or resolving symbols in a ReSharper backend — and a name
  already says what colour it is.
- **Clicking the frame to select a widget.** The reverse direction (tree → outline) works; going the
  other way needs hit-testing in the app, which it already has behind <kbd>Shift</kbd>+<kbd>D</kbd>.
