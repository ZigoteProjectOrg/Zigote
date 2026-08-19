# SVG

`SvgPicture` draws an SVG at exactly the pixels it occupies, rasterized by
[resvg](https://github.com/linebender/resvg). Resize the widget — or move the window to a
different-density display — and it re-rasterizes, so it stays sharp where a PNG in an `Image`
would not.

```csharp
using Zigote.UI.Svg;
using Zigote.UI.Widgets.Controls;

new SvgPicture(icon) { Width = 24, ColorFilter = theme.OnSurface, AltText = "Settings" }
```

The API mirrors flutter_svg (the widget) and jovial_svg (the reusable parsed document):

| | |
|---|---|
| `SvgPicture.FromAsset("icons/logo.svg")` | from the deployed `Assets/` tree — pass a literal, the [asset shake](assets.md) matches on it |
| `SvgPicture.FromFile` / `FromBytes` / `FromString` | from disk, memory, or an inline document |
| `new SvgPicture(SvgAsset)` | from a document you parsed yourself, and keep |
| `Width` / `Height` | either one alone keeps the document's aspect; neither means its own size |
| `ColorFilter` | tint every pixel — flutter_svg's `colorFilter`, for a monochrome icon |
| `AltText` | announced to assistive tech; null (default) marks the picture decorative |

**Dispose it**, like `Image`: the texture it holds is freed by nothing else. Disposing also
disposes the document when the widget parsed it itself; an `SvgAsset` you passed in stays yours.

## Parse once, draw many

`SvgAsset` is the parsed document. Parsing is the expensive half — CSS cascade, text shaping,
`use`/marker/gradient resolution — and rasterizing the same asset again is just filling paths, so
an icon that appears on every row of a list should be parsed once:

```csharp
var icon = SvgAsset.FromAsset("icons/check.svg");   // once, at startup
...
new SvgPicture(icon) { Width = 16 }                 // per row, no reparse
```

`SvgAsset` also exposes the raster directly: `Rasterize(w, h)` returns straight-alpha RGBA8 and
touches no GPU, so a large illustration can be rasterized on a worker thread and handed to
`Image.SetTexture`. `SvgPicture` itself rasterizes during layout, on the UI thread — right for
icons, not for a 4K illustration.

## Compiled SVG

`zigote-svgc` resolves a document ahead of time and writes it back out with no CSS, no text and no
inheritance left to resolve:

```
cargo run --release --bin zigote-svgc -- icon.svg Assets/icons/icon.svg
```

The output is still SVG, so nothing downstream changes — the same `SvgPicture.FromAsset` loads it.
This is jovial_svg's `.si` trade without a second format to load; the same step is available
in-process as `SvgAsset.Compile(bytes)`.

The win is proportional to what there was to resolve, so compile the documents that have work in
them and leave plain path art alone (measured on the samples the gallery's SVG page loads, warm
parse):

| Document | Authored | Compiled |
|---|---|---|
| `Steps.svg` (text + stylesheet) | 16 kB, 0.24 ms | 12 kB, **0.03 ms** |
| `displayWebStats.svg` (text-heavy) | 86 kB, 1.5 ms | 1.3 ms |
| `tiger.svg` (paths only) | 97 kB, 1.4 ms | 111 kB, 1.4 ms — a wash, and bigger |

Text is the case that always pays, beyond its own parse: resolving `<text>` needs the system font
database, and enumerating it costs ~20 ms once per process. A compiled document has no text left,
so an app that ships only compiled SVGs never triggers that (the binding checks the bytes for a
text element before deciding to load fonts at all).

## Seeing it run

The widget gallery's **SVG** page (`Zigote.UI.Gallery/Pages/SvgPage.cs`) is the live demo: a tiger
whose size animates between 96 and 260 px — re-rasterized at every frame it passes through — one
parsed star tinted five ways, a sideways-scrolling strip of sixteen samples fetched from
[dev.w3.org](https://dev.w3.org/SVG/tools/svgweb/samples/svg-files/) through a `Zigote.Http` runner with a disk cache, and
the compiled-vs-authored parse timings above, measured on the spot rather than quoted.

## Building the native library

The binding lives in `native/zigote-svg` (a small C ABI over resvg) and is built by
`build/Zigote.Svg.targets`, imported by `Zigote.UI`. Unlike the Zig engine it is **optional**: a
checkout without a Rust toolchain builds and runs fine, and only `SvgPicture` fails — with a
`DllNotFoundException` — until `cargo` is installed. iOS is not wired up yet: a cdylib cannot be
loaded from an app sandbox there, so it needs the static-link treatment the engine already has.
