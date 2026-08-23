# The Zigote SDK: versions, packages and compatibility

An app does not depend on "the Zigote checkout next door". It depends on **one version string**,
and everything the app builds against — the C# libraries, the native engine, the Android support
files — ships under that string.

```xml
<PropertyGroup>
    <ZigoteSdkVersion>0.1.0</ZigoteSdkVersion>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="Zigote.UI" Version="$(ZigoteSdkVersion)"/>
    <PackageReference Include="Zigote.UI.Material" Version="$(ZigoteSdkVersion)"/>
    <PackageReference Include="Zigote.Engine.runtime.$(ZigoteRuntimeRid)" Version="$(ZigoteSdkVersion)"/>
</ItemGroup>
```

That is what `zigote create` writes. Upgrading is editing one line; trying a newer SDK without
editing anything is `dotnet build -p:ZigoteSdkVersion=0.2.0`.

## What ships

| Package | What it carries |
|---|---|
| `Zigote.UI`, `Zigote.UI.Material`, `Zigote.UI.DevTools`, … | The managed framework, and the bundled fonts (as content, copied next to your executable) |
| `Zigote.Runtime`, `Zigote.Core`, `Zigote.Game`, … | The engine-facing managed layers, restored as dependencies |
| `Zigote.Engine.runtime.<rid>` | The **native** engine for exactly one RID — `libzigote` plus `zigote_svg`, at `runtimes/<rid>/native/` |
| `Zigote.Android.Support` | SDL's Java sources, the fonts as APK assets, the hot-reload switch |

The native engine is per-RID on purpose: an app restores the one binary it runs, and building for a
phone needs **no Zig toolchain, no NDK and no Android sysroot** — the cross-compile happened once,
where the engine was released.

## The compatibility rule

**The managed packages and `Zigote.Engine.runtime.<rid>` must be the same version.** They are two
halves of one ABI: C# structs on one side, Zig structs on the other, checked at startup
(`RendererAbiInfo`). A mismatch restores cleanly and then throws on the first frame, which is a long
way from the line that caused it — so:

- keep every Zigote `PackageReference` on `$(ZigoteSdkVersion)`, never on literals that can drift;
- `zigote doctor` reads the projects in the current directory and fails if they disagree.

Within one version, the C# API follows normal semantic versioning: a patch is a fix, a minor adds,
a major may break. The **ABI** between managed and native is not a public interface and carries its
own number (`RendererAbiInfo.ExpectedAbiVersion`); it changes whenever it has to, which is exactly
why the two halves are versioned together rather than resolved independently.

## Which RID an app builds for

`$(ZigoteRuntimeRid)` decides which engine package is referenced. The generated project resolves it
in this order:

1. `-p:ZigTargetRid=…` — what `zigote run` and `zigote build` pass, and what an Android head must
   have (`android-arm64` for a device, `android-x64` for the emulator);
2. an explicit `RuntimeIdentifier`;
3. the SDK's own host RID, so a plain `dotnet build` on a laptop does the obvious thing.

## Building against a checkout instead

Engine work needs sources, not packages:

```sh
zigote create MyApp --engine ~/src/Zigote     # ProjectReferences into the checkout
```

`zigote create` also does this on its own when you scaffold **inside** a checkout — that directory
is the engine's own repo, and an app created there is almost always a test of the engine. A checkout
next door is *not* enough: binding an app to whatever branch happens to be checked out beside it is
how "it built on my machine" starts.

In checkout mode Zig and Rust are required (they build the native halves); `zigote doctor` checks
for them only there.

## Releasing the SDK

The version lives in **one file**, `build/version.txt`. A release edits it; a prerelease adds a
suffix instead of editing anything.

```sh
build/pack.sh linux-x64 osx-arm64 android-arm64 android-x64      # 0.1.0, unsigned, local
build/pack.sh --suffix rc.1 linux-x64                            # 0.1.0-rc.1
build/pack.sh --version 0.2.0 --sign --push nuget.org linux-x64  # the real thing
```

`build/pack.sh` packs the managed closure (no native toolchain needed, `-p:ZigoteManagedPack=true`),
then one engine package per RID, then the Android support package. Everything lands in
`artifacts/packages/` under a single `$(ZigoteVersion)` — the CLI included, so the tool and the
packages it scaffolds against can never disagree.

### Provenance: what a published artifact can prove about itself

| Artifact | Carries |
|---|---|
| Every assembly | `InformationalVersion` with the commit id, SourceLink to the repository, a deterministic build |
| Every `.nuspec` | version, repository URL, commit |
| Every engine package | `runtimes/<rid>/native/zigote-engine.json` — version, commit, optimize level, and a SHA-256 per native file |
| `artifacts/packages/SHA256SUMS` | a checksum per published `.nupkg` |

`zigote doctor` verifies the engine manifest against the binaries an app actually built with, so a
half-copied or hand-replaced `libzigote` is a named finding instead of a crash inside the engine.

### Signing

```sh
export ZIGOTE_SIGN_CERT=~/secrets/zigote-release.pfx
export ZIGOTE_SIGN_CERT_PASSWORD=…        # or ZIGOTE_SIGN_FINGERPRINT for a cert in the store
export ZIGOTE_TIMESTAMPER=http://timestamp.digicert.com   # the default
build/pack.sh --sign linux-x64
```

`--sign` author-signs every package with `dotnet nuget sign` and then runs `dotnet nuget verify` over
the whole set — an unsigned package in a batch is exactly the one that gets pushed by accident.
Timestamping is not optional: it is what keeps a signature valid after the certificate expires.

`--push` refuses to run without `--sign`, and needs `NUGET_API_KEY` in the environment. A consumer
checks a download with:

```sh
sha256sum -c SHA256SUMS
dotnet nuget verify Zigote.UI.0.2.0.nupkg
```

To try the result before publishing:

```sh
dotnet nuget add source "$PWD/artifacts/packages" -n zigote-local
zigote create MyApp && cd MyApp && zigote run
```

## Shipping a desktop app

```sh
zigote build appimage                       # Linux, this machine's architecture
zigote build appimage --aot                 # …as one native binary, no .NET install
zigote build exe --rid win-x64              # Windows
zigote build app                            # macOS, host architecture
```

`build` publishes self-contained, then wraps. For an AppImage that means a full AppDir — `AppRun`,
a `.desktop` file, and the icon at the root — handed to `appimagetool` with zstd compression. Every
piece is mandatory and three of them fail *late*: no `.desktop` and appimagetool refuses, an
`Icon=` naming nothing gives a blank launcher entry, and an `AppRun` pointing at the wrong file
gives an app that does nothing when clicked. (That last one is not hypothetical — a self-contained
publish also contains the runtime's `createdump` helper, which sorts before most app names.)

### Icons

One square `icon.png` next to the project, or `--icon <file>`. Every Android density and the
AppImage's 256×256 are regenerated from it — the rasteriser is built into the CLI, so there is no
package to install and no five hand-exported sizes to drift apart.

An app with **no** icon still gets one: a generated placeholder, a rounded square tinted from the
app's own name. Not art — but a launcher full of generic grey sheets reads as a broken install,
and two placeholder apps on one launcher are still telling apart.

### Size

| Knob | Default | What it costs |
|---|---|---|
| Debug symbols (`DebugType=none`) | off in release | nothing — crash reports are served by the published `.snupkg` |
| Reference `.xml`/`.pdb` files | dropped in release | nothing for a shipped app |
| Asset shaking | on in release | nothing — it drops content the code cannot reach |
| AppImage compression | zstd | smaller *and* faster to mount than the gzip default, so it is startup time too |
| `--aot` | opt-in | host architecture only; no app that loads assemblies at runtime |
| `--strip-fonts` | **opt-in** | Latin + Cyrillic only — any other script renders as tofu |

Trimming is deliberately not offered: the UI stack reflects over widget and theme members (the
inspector, the titlebar hit-test), and a trimmed build fails at runtime, in one widget, on someone
else's machine. NativeAOT is the exception, because `--aot` goes through the rooting in
`build/Zigote.Aot.targets` — which the `Zigote.UI` package ships, so an app built from the SDK gets
it too.

`--strip-fonts` needs `hb-subset` (harfbuzz) or `-p:FontSubsetTool=pyftsubset`. Iosevka's bundled
six-face `.ttc` alone is ~66 MB, so it is the single largest thing in a Zigote artifact — and the
reason it is not on by default is that no amount of testing in English reveals the tofu.

Measured on this repo (linux-x64, release):

| Artifact | AppDir | AppImage |
|---|---|---|
| `Zigote.Editor` (JIT — it compiles user scripts, so it cannot AOT) | 180 MB | **63 MB** |
| `Zigote.UI.Adwaita.Gallery` (`--aot`) | 105 MB | **33 MB** |

Anything `build` does not cover, `--property name=value` passes straight through to `dotnet
publish`.
