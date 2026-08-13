# Assets

Files a Zigote app loads at runtime by path — sprites, textures, audio, data — and how the build keeps
the ones it ships down to the ones it needs.

Two separate things share the word "asset" in this engine, and they do not overlap:

| | Where it lives | Who resolves it | How it is shaken |
|---|---|---|---|
| **App assets** (this document) | `Assets/` next to the executable | `AppAssets` (`Zigote.UI`) | Publish-time, from the app's compiled string literals |
| **Game content** | `Content/` in an exported game | `ContentFiles` (`Zigote.Runtime`) | Export-time, from the scene graph (`AssetDependencyGraph`) |

A 2D UI app wants the first. A game exported from the editor gets the second for free — its assets are
reachable from a scene, so the exporter already stages only what the scene touches.

## Declaring assets

```xml
<ItemGroup>
  <ZigoteAsset Include="Assets/**"/>
</ItemGroup>
```

Every matched file is deployed to `Assets/<same relative path>` next to the executable, on build and on
publish, and on iOS/Android through that platform's own resource item type. The deployed folder is
always `Assets/` regardless of where the files sit in the project, so one path string works everywhere.

The glob is **not** implicit. `Assets` is also an ordinary C# namespace folder in this repo
(`Zigote.Core/Assets/` holds `AssetManager`, `AssetRegistry`, `AssetPath`), and a default include would
quietly deploy that source tree as runtime content.

Assets flow transitively, exactly as `Content` does: a widget library that ships its own `Assets/` tree
lands it in every app that references the library — the mechanism that already puts `Zigote.UI`'s fonts
next to every binary.

## Reading them

```csharp
var sprite = Image.FromAsset("Sprites/hero.png");     // widget
var bytes  = AppAssets.ReadAllBytes("Data/levels.json"); // raw
var path   = AppAssets.Path("Audio/Ui/click.wav");       // for APIs that take a path
```

`AppAssets.Root` probes both `AppContext.BaseDirectory` and the real executable's directory, plus the
macOS `Contents/Resources` location — a self-extracting single-file build reports its extraction
directory under `~/.net` as `BaseDirectory` while the assets sit beside the executable, so probing only
one finds nothing in exactly the configuration that is hardest to debug.

For a UI that streams many assets, `AppAssets.CreateManager(registry)` returns the ref-counted,
deduplicating `AssetManager` from `Zigote.Core.Assets` rooted at `Assets/` — the same cache the editor
uses, not a second one.

> `Image.FromAsset` and `Image.FromResource` are different mechanisms. `FromResource` takes an
> **embedded resource** name out of an assembly manifest; `FromAsset` takes a path inside the
> deployed `Assets/` tree. They are deliberately separate names rather than overloads: C# prefers the
> candidate with fewer omitted optional parameters, so a one-argument embedded-resource call would
> silently have started resolving as a file path.

## Tree shaking

`EnableAssetShaking` deletes, at publish time, every file under `Assets/` and `Fonts/` that the app's
compiled code cannot name — and derives the font subset ranges from the same scan.

```xml
<EnableAssetShaking>true</EnableAssetShaking>
```

Reachability comes from the string literals in the `#US` metadata heap of the app's own assembly and its
`Zigote.*` reference closure (`tools/AssetShake.cs`). IL rather than source, because it needs no source
access, picks up generated code for free — the `.arb` → `GalleryL10n` catalogues become literals, which
is why the Gallery's Russian text keeps its Cyrillic in the font subset without anyone listing a range —
and reads identically for a JIT and a NativeAOT publish. (It scans the *intermediate* assemblies: an
AOT bundle has no managed assemblies left in it to read.)

A file is kept if a literal equals or contains its relative path, its file name **or its stem**. Stems
matter: `Zigote.UI` builds font paths as `faceName + ".ttf"`, so `"Inter-Medium"` is the only literal
that exists for that file. Ambiguity always keeps the file.

Font licences are paired with their font: `LICENSE-NotoEmoji-OFL.txt` ships only while a `NotoEmoji*`
file does, since the OFL attaches to redistribution of the font itself.

### Font subsetting is its own switch

Shaking decides which font *files* ship; **`EnableFontStripping`** (`build/Zigote.Fonts.targets`)
rewrites the kept ones down to the glyphs the app can reach. It is independently opt-in, and it
needs HarfBuzz's `hb-subset` on the build machine (`brew install harfbuzz`; point `FontSubsetTool`
elsewhere or add flags with `FontSubsetExtraArgs`). The codepoint ranges come from `FontUnicodes` —
set it by hand, or turn on shaking and let the same literal scan derive it. The 66 MB → 0.66 MB row
below is **both** switches on; shaking alone only drops whole files. `ZigoteShakeRoots` (default
`Assets;Fonts`) names the directories shaking is allowed to delete from.

### What it cannot see

A path assembled at runtime — `$"Assets/Icons/{name}.png"` — is two literals that name no file, so the
file it opens looks unreachable. That is the entire soundness boundary, and the reason this is opt-in
per app:

```xml
<ZigoteShakeKeep>Assets/Icons/*;Assets/Levels/*</ZigoteShakeKeep>
```

Everything dropped is named in the build log, so a mistake is visible rather than silent, and
`ZigoteShakeDryRun` reports without deleting. **The editor must never enable this** — it composes paths
and loads user scripts at runtime.

For fonts specifically, a dropped codepoint is usually not fatal: the glyph router falls back to the
system faces `SystemFonts` registers (Hiragino/PingFang on macOS, Yu Gothic/Segoe UI on Windows,
fontconfig elsewhere) — and, for emoji, to the platform's own colour face (Apple Color Emoji, Segoe
UI Emoji, a system Noto Color Emoji) when the app bundles none. Two exceptions are worth knowing:

- Below **U+0100** there is no fallback — the router returns the primary face without consulting them —
  so that range is always kept, regardless of what the scan found.
- Text the app renders but never names in code (a script users are expected to *type*, rather than one
  the app draws itself) is invisible to the scan. Add it with `ZigoteShakeKeepUnicodes`, e.g.
  `U+0400-04FF` for Cyrillic input.

### Measured effect

Publishing the Adwaita gallery for `osx-arm64` with shaking and font subsetting on:

| | Before | After |
|---|---|---|
| `Fonts/` | 66.19 MB (12 files) | 0.66 MB (10 files) |
| Bundle (self-contained JIT) | 168.1 MB | 102.6 MB |
| Bundle (NativeAOT) | 104.7 MB | 46.0 MB |

Only the `Fonts/` row is a property of this feature; the bundle totals also move with whatever else the
app links, so treat them as a snapshot rather than a target. (The snapshot predates the emoji
fallback pass: `App.cs` now loads `NotoEmoji-Regular.ttf` as the last-resort monochrome emoji
fallback behind the platform's colour face, so that file — and its paired OFL licence — survive
shaking today.)
