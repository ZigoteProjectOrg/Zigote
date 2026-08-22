# `zigote` CLI — design for the gap to Flutter, Compose and Xcode

*Written 2026-08-22, against `Zigote.Cli` at `create` / `add` / `preview` / `device` / `doctor`.*

The tool today does one thing very well: it writes the Android head, the part nobody can type
from memory. That was the right first thing. It is not the thing a newcomer measures us on.

What a newcomer measures is the first hour. `flutter create x && cd x && flutter run` is three
words and a hot-reload keystroke, on whatever device happens to be plugged in. Compose gives them
Android Studio doing the same behind a green triangle. Xcode gives them a scheme picker and
⌘R. Our equivalent is: clone the engine somewhere the scaffolder can find it, `dotnet run` for
desktop, `zigote device run` for Android, `zigote preview` for a single widget, nothing at all for
iOS, and `dotnet publish` incantations for a shippable artifact.

The gap is not features. It is that **we have three doors where they have one, and the door needs
a git checkout behind it.** Everything below follows from those two sentences, in the order the
work should happen.

## The two structural items

### 1. Ship an SDK, not a checkout path

`CommonVerb.ResolveEngine` walks up from the target directory looking for
`Zigote.UI/Zigote.UI.csproj`, falls back to `$ZIGOTE_ROOT`, and writes a *relative* path into the
generated `csproj`. That is a good implementation of the wrong contract. It means:

- there is no "install Zigote and make an app" — there is "clone Zigote, then make an app *near
  it*";
- the generated project is bound to one machine's directory layout, and moving the app up one
  level breaks it in a way that surfaces as an unresolvable `ProjectReference` much later;
- there is no version. An app does not depend on *Zigote 1.4*, it depends on whatever is checked
  out next door, including someone's half-finished branch;
- CI has to reproduce the layout, not restore a package.

Flutter ships an SDK with a pinned version per app (`flutter --version`, `.flutter-version`);
Xcode ships one per Xcode; Gradle resolves Compose from Maven with a version literal in the build
file. All three answer "what is my app built against" with a string.

**The change.** `create` emits `PackageReference`s to `Zigote.UI`, `Zigote.Runtime` and the native
runtime package for the target RIDs, at the CLI's own version. `--engine <path>` stays, and is what
engine developers pass to get today's `ProjectReference` behaviour; the automatic upward walk
becomes a convenience *inside* a checkout only, never the default for a user-created app. The
native engine ships as RID-specific packages (`Zigote.Engine.runtime.android-arm64` and
friends) so a mobile build stops requiring a Zig toolchain on the build machine — `doctor`'s Zig
check becomes engine-developer-only, which is a good measure of whether this landed.

This is the largest item and it gates the rest. "Better than Flutter" is not reachable while the
on-ramp is `git clone`.

### 2. One `run`

```
zigote run                 # the only attached device, else desktop
zigote run --device pixel  # by name, serial or id — same picker as `device list`
zigote run --release
```

Everything the three current commands do becomes flags on this one:

| Today | Becomes |
|---|---|
| `dotnet run --project X` | `zigote run` with no device attached, or `--device desktop` |
| `zigote device run` | `zigote run --device <android serial>` (auto-selected when it is the only one) |
| `zigote preview Foo.Bar` | `zigote run --widget Foo.Bar` |
| `zigote device logs` | the log stream `run` already prints, plus `zigote logs` to re-attach |

`device list` survives as the enumerator (and gains simulators and desktop), `device run` becomes
an alias we keep for one release and delete. The interactive keys are the part people actually
remember Flutter for: `r` reload, `R` restart, `q` quit, `d` detach, printed once at start. The
hot-reload machinery already exists behind `zigote device run` and `dotnet watch`; this is a
front-end, not a new capability.

**Device selection rule.** Exactly one candidate → use it silently. More than one → print the
numbered list and ask, unless `--device` was given. Zero mobile devices → desktop. Never guess
between two.

## The gaps that follow

### 3. `zigote build` — the shippable artifact

There is no verb that produces something you can hand to a store or a user. Today that is
`dotnet publish` with the right RID, the right configuration, the right signing properties, and no
help if you get one wrong.

```
zigote build apk|aab|ipa|app|exe|appimage [--release] [--sign …]
```

with three things Flutter makes people reach for packages to get:

- **icons and splash from one source image.** `zigote build` regenerates every density, adaptive
  icon layer and launch image from `icon.png` / `splash.png` declared in the project. This is
  `flutter_launcher_icons` + `flutter_native_splash`, two of the most-installed packages on
  pub.dev, and it is a rasteriser we already own — `Zigote.Render2D` and the resvg binding do this
  in a dozen lines.
- **version from one place.** `<Version>` in the app csproj drives `versionCode`/`versionName`,
  `CFBundleShortVersionString`, and the desktop file metadata. Three files disagreeing about the
  version number is a release-day bug in every framework here.
- **signing that says what is missing.** Unsigned release build → name the keystore property that
  is absent and the `keytool` line that creates it, in `doctor`'s voice.

### 4. iOS, at parity with Android

`Templates.cs` already carries iOS plugin channels, and `add` refuses everything but android.
`zigote add ios` writing the head, `device list` enumerating simulators via `simctl`, and
`zigote run` deploying to them is the difference between a mobile framework and half of one. The
Android head taught us the shape; the iOS one is a smaller version of the same lesson (an
`AppDelegate`, the SDL glue, an `Info.plist` whose keys have to match the plugins in use, and a
RID that couples managed to native).

### 5. `doctor --fix`

`doctor` already knows the cure for every finding — it prints it in the fix column. Running it is
a strictly smaller diff than the current design's ambition, and it is where we beat `flutter
doctor`, a command famous for telling people to accept Android licences instead of accepting them.

```
zigote doctor --fix   # install the workload, accept the licences, fetch the JDK
```

Each check declares an optional fix command alongside the string it already declares. Prompt once
with the list, then run them in order. Never fix silently, never fix without `--fix`.

### 6. `--json` everywhere

`tools/rider` parses human stdout today, which means every phrasing change in this tool is a
potential IDE-plugin bug. `flutter --machine` exists for exactly this reason. `device list`,
`preview --list`, `doctor` and `run`'s progress events grow a `--json` flag emitting one JSON
object per line. Cheap, and it is the precondition for VS Code / Rider integration that does more
than shell out.

### 7. `zigote test` — goldens

`Zigote.SmokeTest` already renders golden images. `flutter test --update-goldens` is a headline
feature of the framework it is copied from, and ours is currently a project you have to know
exists. Expose it: `zigote test [--update-goldens]`, running the app's own widget tests and
diffing images with the failure written out as a three-panel PNG (expected / actual / diff).
Building nothing new — surfacing what is built.

### 8. `zigote run --inspect`

`Zigote.UI.DevTools` is an in-app overlay on <kbd>Shift</kbd>+<kbd>D</kbd>; the inspect protocol
behind `tools/rider` and the MCP server already streams the widget tree out of a running app.
Nothing in the CLI launches either. `--inspect` starting the app with the protocol enabled and
printing the URL is the Flutter-DevTools / Compose-preview parity item, and it is a flag, not a
feature.

## Non-goals

- **`analyze` / `format`.** `dotnet format` and the analyzers already run in the IDE and in CI.
  Wrapping them adds a name to learn and nothing else.
- **A package registry.** NuGet is the registry. `flutter pub` exists because Dart needed one.
- **A GUI.** Android Studio and Xcode are the competition's GUI, and the Rider plugin is our
  answer. The CLI's job is to be the thing the GUI calls.
- **Templates beyond `app` and `plugin`.** A "package" template is `dotnet new classlib` plus a
  `PackageReference`. Add one when someone asks twice.

## Order, and why

1. **SDK packages** (#1) — gates everything; without it the other items polish a door nobody can
   open.
2. **`zigote run`** (#2) — the first-hour experience, and it is mostly a front-end over machinery
   that exists.
3. **`build`** (#3) — the last-hour experience. Icons and versioning first; signing behind them.
4. **iOS** (#4) — coverage, and the biggest single chunk of new code here.
5. **`doctor --fix`, `--json`, `test`, `--inspect`** (#5–8) — each is a day, each is independent,
   any of them can jump the queue when something else needs it.

The measure of success is not a feature count. It is that the README's getting-started section
becomes three lines with no clone in them, and that `zigote run` is the only command in it.
