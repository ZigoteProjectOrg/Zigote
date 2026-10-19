# The Renderer

Everything you have seen in this app, including the text you are reading, reaches the screen through one native renderer written in Zig on **wgpu-native**. wgpu runs on Metal on macOS and iOS, on Direct3D 12 or Vulkan on Windows, and on Vulkan or GL on Linux and Android. It is the only backend: there is no software fallback and no per-API renderer.

## From PaintList to pixels

1. Widgets append records to the frame's `PaintList`. The list is a **tagged byte stream**: each record is a small header (`kind`, `size`) followed by a struct specific to that kind. A rectangle is 48 bytes. Text and pixel payloads live in pinned buffers referenced by address, so a steady frame allocates nothing on the C# side.
2. `ZigoteEngine` submits the stream in one call. The engine walks it once and does not copy it into another format.
3. The 2D renderer (`renderer/wgpu.zig`) tessellates the commands into batches: instanced rounded rectangles, glyph quads from a shared atlas, polygons, Béziers, shadows, clips and opacity groups.
4. If the frame has 3D content, the scene renders first and the UI is composited over it.
5. One command encoder records every pass; the frame is submitted and presented once.

```
C#                               │  Zig (libzigote)
Widget.Paint → PaintList stream  │  walk stream → batch → wgpu passes → present
          zigote_submit_paint_commands(stream, len)
```

## Text

Text never becomes a bitmap in C#. A `TEXT` record carries UTF-8 and a style. Natively, HarfBuzz shapes it (ligatures, kerning, bidi runs, emoji sequences), FreeType rasterises each glyph once into a GPU atlas, and shaped runs are cached by content, so re-painting an unchanged label is a cache hit. Glyph blending is done in linear light with a perceptual alpha remap, which is why text looks the same weight on light and dark backgrounds.

## Partial repaint

The renderer keeps the previous frame's stream and diffs the new one against it. Changed commands produce **damage rectangles**, and only those regions are scissored and redrawn. A blinking caret, a spinner or a hover glow touches a few hundred pixels, not the whole window.

## Effects

| Effect | How |
|---|---|
| Rounded clips | An SDF clip uniform; a clip's radius rides on the command |
| Shadows | Analytic Gaussian, no blur pass |
| Backdrop blur | A separable blur over a copy of the target region |
| Liquid Glass | A refraction shader: squircle bevel, bounded lens, rim and vibrancy over the backdrop |
| Custom shaders | `AddShaderEffect` with a registered WGSL shader id |

## 3D

The 3D renderer (`renderer/wgpu_3d.zig`) is created lazily the first time 3D content is uploaded; a pure UI app never allocates it. It is a forward+, EEVEE-style pipeline:

```
shadow → sky → geometry + G-buffer → SSAO/GTAO → SSR → bloom → AgX tonemap → TAA → UI → composite
```

Every tunable is carried in one `ZgRenderSettings3D` struct. See *Games & 3D*.

## The ABI guard

C# and Zig must agree byte-for-byte on every struct that crosses the boundary. At startup `RendererAbiInfo.Validate` compares the engine's reported ABI version (currently **100**, the V2 contract) and the sizes of the paint-op header, `ZgEvent` and handles against the C# definitions, and refuses to run on a mismatch instead of corrupting memory. On the Zig side, `src/abi.zig` pins every field offset with `comptime` asserts.
