# Third-Party Notices

Zigote is licensed under the [MIT License](LICENSE). It incorporates the third-party
components below. Each entry lists the license and where the full license text lives in
this repository (or in the referenced package). If you redistribute Zigote or an
application built on it, these notices and license texts must accompany the distribution.

## Native engine (`Zigote.Engine`, compiled into `libzigote`)

| Component | Version / pin | License | License text |
|---|---|---|---|
| [SDL3](https://libsdl.org) (via the `sdl3` Zig binding) | binding 0.2.1 | zlib | `Zigote.Engine/zig-pkg/sdl3-*/LICENSE` |
| [wgpu-native](https://github.com/gfx-rs/wgpu-native) (prebuilt binaries) | v29.0.1.1 | MIT OR Apache-2.0 | `Zigote.Engine/libraries/wgpu/wgpu-native.LICENSE.MIT`, `wgpu-native.LICENSE.APACHE` (the Zig binding under `libraries/wgpu` is first-party, MIT like the rest of Zigote) |
| [FreeType](https://freetype.org) (built from source via allyourcodebase/freetype) | 2.14.3 | FreeType License (FTL) | fetched at build time (license text in the downloaded package); see the attribution below |
| [HarfBuzz](https://harfbuzz.github.io) (built from source via allyourcodebase/harfbuzz) | 14.1.0 | MIT (“Old MIT”) | fetched at build time (license text in the downloaded package) |
| [Jolt Physics](https://github.com/jrouwe/JoltPhysics) (bundled by zphysics) | vendored | MIT © 2021 Jorrit Rouwe | SPDX headers in `Zigote.Engine/libraries/zphysics/libs/Jolt/`; the zphysics wrapper is MIT (`libraries/zphysics/LICENSE`) |
| [flecs](https://github.com/SanderMertens/flecs) (bundled by zflecs) | vendored | MIT | `Zigote.Engine/libraries/zflecs/libs/flecs/LICENSE`; the zflecs wrapper is MIT (`libraries/zflecs/LICENSE`) |
| [miniaudio](https://miniaud.io) (bundled by zaudio) | vendored | public domain (unlicense) OR MIT-0 (dual) | embedded at the end of `Zigote.Engine/libraries/zaudio/libs/miniaudio/miniaudio.h`; the zaudio wrapper is MIT (`libraries/zaudio/LICENSE`) |
| [Assimp](https://github.com/assimp/assimp) (built from the upstream v5.3.1 source archive) | 5.3.1 | BSD-3-Clause | in the fetched source archive; the Zig build wrapper (forked from allyourcodebase/assimp) is BSD-3-Clause © Felix Queißner (`Zigote.Engine/libraries/assimp/LICENCE`) |
| [zlib](https://zlib.net) (built from source via allyourcodebase/zlib; used by Assimp and FreeType) | 1.3.2 | zlib | fetched at build time (license text in the downloaded package) |
| [libwebp](https://chromium.googlesource.com/webm/libwebp) | vendored | BSD-3-Clause © Google | `Zigote.Engine/libraries/libwebp/COPYING` |
| [zmath](https://github.com/zig-gamedev/zmath) | vendored | MIT © Michal Ziulek / zig-gamedev | `Zigote.Engine/libraries/zmath/LICENSE` |
| [zmesh](https://github.com/zig-gamedev/zmesh) (bundles [par_shapes](https://github.com/prideout/par) © Philip Rideout, [cgltf](https://github.com/jkuhlmann/cgltf), and [meshoptimizer](https://github.com/zeux/meshoptimizer) © 2016-2022 Arseny Kapoulkine — all MIT) | vendored | MIT © Michal Ziulek / zig-gamedev | `Zigote.Engine/libraries/zmesh/LICENSE`; bundled libs carry license headers in their sources under `libraries/zmesh/libs/` |
| [zpool](https://github.com/zig-gamedev/zpool) | vendored | MIT © zig-gamedev contributors | `Zigote.Engine/libraries/zpool/LICENSE` |
| [zigimg](https://github.com/zigimg/zigimg) | 0.1.0 | MIT | `Zigote.Engine/zig-pkg/zigimg-*/LICENSE` |
| SDL Linux build deps (X11/Wayland/ALSA headers etc.) | fetched | various (permissive) | fetched at build time (`sdl_linux_deps-*/LICENSES` in the downloaded package) |

Vendored zig-gamedev libraries carry a `ZIGOTE_VENDOR.txt` recording the upstream revision
and any local patches.

### FreeType attribution

Portions of this software are copyright © The FreeType Project (www.freetype.org).
All rights reserved.

## Bundled fonts (`Zigote.UI/Fonts/`, copied into the output of every referencing app)

| Font | License | License text |
|---|---|---|
| [Inter](https://rsms.me/inter/) | SIL Open Font License 1.1 | `Zigote.UI/Fonts/Inter/OFL.txt` |
| [Iosevka](https://typeof.net/Iosevka/) | SIL Open Font License 1.1 | `Zigote.UI/Fonts/PkgTTC-SGr-Iosevka-34/OFL.txt` |
| [Noto Emoji](https://fonts.google.com/noto/specimen/Noto+Emoji) | SIL Open Font License 1.1 | `Zigote.UI/Fonts/Noto_Emoji/OFL.txt` |
| [Material Icons](https://github.com/google/material-design-icons) | Apache License 2.0 © Google | `Zigote.UI/Fonts/MaterialIcons/LICENSE.txt` |

The OFL requires that the license text accompany the fonts wherever they are
redistributed — applications shipping a Zigote.UI build output should keep the
`Fonts/` license files alongside the font binaries.

## Managed (NuGet) dependencies

Resolved by NuGet at build time and not vendored in this repository; licenses are
declared in their packages:

- [FParsec](https://www.quanttec.com/fparsec/) — BSD-2-Clause (the optional
  `Zigote.Modules.UI.CodeEditor` F# syntax-highlighting module)
- [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) — MIT (asset compression in
  `Zigote.Runtime`/`Zigote.Editor` game export)
- [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) — MIT (`Zigote.Editor`)

Test-only and build-time-only packages (xunit, Microsoft.NET.Test.Sdk,
Microsoft.CodeAnalysis.* analyzers) are not redistributed.
