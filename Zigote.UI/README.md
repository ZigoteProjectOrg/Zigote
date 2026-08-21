# Zigote.UI

A **declarative, retained-mode widget framework** in C#. Widgets are long-lived objects: you build the tree once and
mutate properties per frame — hover, press, focus, scroll, and animation state live on the widgets themselves, so there
is no diff/reconcile pass to re-derive it. It renders through the Zig/wgpu backend via `Zigote.Core` and depends on
**nothing above the GPU** — no scenes, scripting, or editor — so it is usable standalone and headless-testable (build a
tree, lay it out, dispatch synthetic input, assert the emitted `ZgPaintCommand`s; no native window required).

The design language is **flat, minimalist macOS**: opaque surfaces layered by elevation, a small accent-tinted palette,
the SF-style type ramp, an 8-pt spacing grid, and soft low-opacity shadows. Translucency ("Liquid Glass") is opt-in.

## The model

Every frame runs four phases:

```
Measure(Constraints) → Layout(Offset) → DispatchEvents → Paint(PaintList)
```

- **Measure** — bottom-up; returns a `Size`, cached internally.
- **Layout** — top-down; sets each widget's absolute `Bounds`.
- **DispatchEvents** — after layout, so `Bounds` is valid for hit-testing.
- **Paint** — emits flat `ZgPaintCommand` structs into a reused buffer (0 B/frame on the steady path).

**Never recreate widgets each frame** — fields on a widget *are* its state.

```csharp
clickBtn.Label = $"Clicked {count}×";              // good — same instance, keeps hover/press
app.Root = new Button($"Clicked {count}×", ...);   // bad  — new widget loses interaction state
```

Events don't relayout: `App.Frame()` re-runs Measure/Layout only when something marks the tree dirty, so a mouse-move
repaints but never re-lays-out.

## Quick start

```csharp
new ZigoteApp
{
    Title = "My App",
    Theme = ThemeData.Dark,
    Home  = new MyHomePage(),
}.Run();
```

`ZigoteApp` boots the engine, injects `ThemeProvider` + `MediaQuery` as root context, and wraps `Home`
in a root `Navigator` so `context.Push`/`Pop` work anywhere. For a raw loop, use `UiApp`:

```csharp
using var app = new UiApp("Title", 1024, 720) { Theme = ThemeData.Dark };
app.Root = new ColoredBox(app.Theme.Background) { Child = /* tree */ };
while (!app.ShouldQuit) app.Frame();   // Measure → Layout → Events → Paint
```

> **Root background must be opaque** — wgpu clears with alpha 0, so wrap your root in
> `ColoredBox(theme.Background)` or the window is transparent on macOS.

## Widget kinds

```csharp
// ComposedWidget — pure function of its props; Build() is cached (call Invalidate() to rerun)
class MyCard : ComposedWidget
{
    protected override Widget Build(BuildContext ctx) =>
        new Card(Theme.Of(ctx)) { Child = new Label("Hello") };
}

// Interactive — a widget's fields ARE its state; mutate the retained children in place
class Counter : ComposedWidget
{
    private readonly Label _label = new("0");
    private int _count;
    protected override Widget Build(BuildContext ctx) => new Column
    {
        Children = { _label, new Button("Increment", () =>
        {
            _label.Text = (++_count).ToString();
            MarkNeedsLayout();          // relayout + repaint; Build does not re-run
        }) }
    };
}
```

- **`MarkNeedsPaint()` < `MarkNeedsLayout()` < `MarkNeedsBuild()`/`Invalidate()`** — pick the cheapest that covers the
  change. Only the last re-runs `Build`.
- **`OwnEffect(() => …)`** is the fine-grained path: a signal-tracked effect that writes into retained children,
  allocating nothing. **`Watch`** is for when the tree's *shape* depends on a signal.
- **`OnMount`/`OnUnmount` + `Own(...)`** scope subscriptions and tickers to the time the widget is actually in the tree.
- **`InheritedWidget`** (`ThemeProvider`, `MediaQuery`) propagates data down the tree; consumers read it with
  `BuildContext.DependOn<T>()` and rebuild only when it changes.
- **Compose by default** — controls are built from the layout kernel + `DecoratedBox` (background) +
  `Pressable` (interaction). Only genuine primitives (layout containers, animated thumbs, canvases, text editors,
  virtualized lists) hand-write `Measure`/`Layout`/`Paint`.

## What's in the box

| Area                                     | Highlights                                                                                                                                                                                                                                                                                                                                                                                                                         |
|------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Layout** (`Widgets/Layout/`)           | `Column`/`Row`/`Expanded`/`Stack`/`Wrap`, `Padding`/`Align`/`Center`, `SizedBox`/`ConstrainedBox`, `DecoratedBox`, `Opacity`/`ClipRect`/`Transform`, `InteractiveViewer` (drag-pan, pinch/⌘-wheel zoom, double-tap), `ScrollView`/`ListView` (virtualized; `ListView.Builder`/`GridView.Builder` build rows on demand, `GridView.Rebind` grows one in place), `PagedListView<T>` (loads page by page as the tail comes into view), `StaggeredGrid` (fixed columns, tiles keep their own height) |
| **Controls** (`Widgets/Controls/`)       | `Label`, `Text`, `RichText`, `SelectableText`, `Button`, `Card`, `Dialog`, `Snackbar`, `Tooltip`, `ContextMenu`, `Icon`, `Image`, `GestureDetector`, `Pressable`, `Skeleton` (shimmering loading placeholder)                                                                                                                                                                                          |
| **Form controls** (`Zigote.UI.Material`) | Deliberately **not** in the kernel — `Zigote.UI.Material` is the framework's control library: `TextField` (single/multiline + IME), `Checkbox`, `Radio<T>`, `Switch`, `Slider`, `Dropdown<T>`, `TabBar`/`TabView`, `ProgressBar`, `Badge`, `NumberInput`, `SegmentedControl`, `SearchField`, `Chip`, `SplitPane`, `TreeView<T>`, `ReorderableList`, `NavigationSplitView`, `ColorPicker`, `CurveEditor`, `GradientEditor`, `CodeEditor` |
| **Liquid Glass** (`Widgets/LiquidGlass/`) | `LiquidGlass` surfaces over media — per-pixel backdrop anchoring, theme-adaptive glass family — plus `LiquidGlassLayer`, `LiquidGlassBlendGroup`, `LiquidStretch`, `GlassGlow`                                                                                                                                                                            |
| **Transitions** (`Widgets/Transitions/`) | Explicit (`FadeTransition`/`SlideTransition`/`ScaleTransition`/`AnimatedContainer`) + implicit (`AnimatedOpacity`/`AnimatedAlign`/`AnimatedPadding`/`AnimatedSwitcher`/`TweenAnimationBuilder<T>`) driven by `AnimationController`                                                                                                                                                                                                 |
| **Animate** (`Widgets/Animate/`)         | flutter_animate-style fluent API: `widget.Animate().Fade(500.ms).Scale(delay: 500.ms)`                                                                                                                                                                                                                                                                                                                                             |
| **Navigation** (`Widgets/Navigation/`)   | Navigator 2.0 — `context.Push`/`Pop`, named routes, declarative `Pages` + `OnPopPage`                                                                                                                                                                                                                                                                                                                                              |
| **Overlays** (`App`, `Widgets/Overlays/`) | `PushOverlay`/`PopOverlay` (painted above and hit-tested before Root); `Popover`                                                                                                                                                                                                                                                                                                                                                   |
| **DragDrop** (`Widgets/DragDrop/`)       | `Draggable<T>` + `DragTarget<T>` (in-app) and OS-file/text drop                                                                                                                                                                                                                                                                                                                                                                    |
| **Menu** (`Widgets/Menu/`)               | One `AppMenu` model → native `NSMenu` on macOS, in-window `MenuBar` elsewhere                                                                                                                                                                                                                                                                                                                                                      |
| **Focus / Semantics**                    | Tab/arrow/Esc traversal (`Focus/`), platform-neutral accessibility tree (`Semantics/`)                                                                                                                                                                                                                                                                                                                                             |

## Theming & design tokens

`ThemeData` is the single source of truth for appearance-dependent colours; appearance-independent scales live in static
token classes under `Theme/` (use a named step, never a magic number):

```csharp
var theme = ThemeData.Dark;   // == ThemeData.MacDark();  .Light == MacLight()
```

| Token class      | Members                                                   |
|------------------|-----------------------------------------------------------|
| `Spacing`        | `Xxs..Xxxl` (2/4/8/12/16/20/24/32)                        |
| `Typography`     | the `TextStyle` ramp (`LargeTitle..Caption`, `Body` = 13) |
| `Radii`          | `Xs..Xl` (3/5/6/8/10) + `Capsule`                         |
| `ControlMetrics` | control heights, checkbox/radio/switch/slider metrics     |
| `Elevation`      | `Z1/Z2/Z3` shadow styles + `paint.AddElevation(...)`      |

Helpers: `Zigote.UI.Text.TextMeasure.Measure/Width` (engine-backed, cached) and
`paint.AddFocusRing(bounds, radius, theme)` for one consistent focus ring.

## Keyboard, focus & accessibility

`App` owns a single focused widget; only it receives `OnKey`/`OnTextInput`/`OnTextComposition`. **Tab/Shift-Tab** cycle
focus in reading order within the active scope (a modal `Dialog` traps it), **arrow keys** do geometric directional nav
(unless the focused control uses arrows internally), **Esc**
dismisses the top overlay. IME composition is wired end to end. Widgets contribute accessibility by overriding
`DescribeSemantics(SemanticsConfiguration)`; the tree is exposed via `App.BuildSemantics()`
and a reserved `ISemanticsBridge` seam.

## Hot reload

Edit a widget's `Build()` while the app runs and the live UI updates without a restart — widget instances and their
fields are preserved, only `Build()` re-runs. Run any
`App`-based app under `dotnet watch`, or use Rider/VS "apply changes". Constructor/field-initializer/
`OnMount` edits and native Zig/shader changes still need a full restart.

## Fonts

Cross-platform fonts are bundled here (`Fonts/`) and flattened into `Fonts/` next to every executable that references
`Zigote.UI` — no system-font dependency. **Inter** (default UI), **Iosevka** (code / monospace), **Material Icons**
(glyphs), **Noto Emoji**.

Three things the engine adds so text always renders as asked:

- **Synthetic bold and oblique.** A face with no bold or italic cut is emboldened/slanted at
  rasterisation time, so `FontWeight.Bold` and italic never silently fall back to regular.
- **Platform colour emoji.** With no colour-emoji font bundled, `SystemFonts` registers the OS's
  own face (Apple Color Emoji, Segoe UI Emoji, a system Noto Color Emoji) — probed for an actual
  raster strike, so an outline-only face is refused rather than swallowing every emoji. Bundled
  **Noto Emoji** stays as the monochrome last resort.
- **Spacing-aware measurement.** `letterSpacing`/`wordSpacing` are applied per cluster in the
  shaper, so measurement and paint always agree.

## Design systems on top

`Zigote.UI` is the flat-macOS base. Two full design languages layer on top of it rather than forking it — they share
the theme, focus, semantics and hot-reload machinery, and mixing them in one app is supported. They stack: Material
doubles as the framework's control library, and Adwaita builds on Material (`AdwEntry` derives from its `TextField`):

- **[`Zigote.UI.Adwaita`](../Zigote.UI.Adwaita/README.md)** — the GNOME Adwaita design system, with live system theming
  and client-side decorations.
- **[`Zigote.UI.Material`](../Zigote.UI.Material/README.md)** — the Material vocabulary with the Flutter names.

Alongside them: **[`Zigote.UI.Charts`](../Zigote.UI.Charts/README.md)** (declarative charting), **[
`Zigote.UI.Localizations`](../Zigote.UI.Localizations/README.md)** (i18n), **[
`Zigote.UI.BottomSheet`](../Zigote.UI.BottomSheet/README.md)**, and **[
`Zigote.UI.DevTools`](../Zigote.UI.DevTools/README.md)**.

## Testing

Everything is headless-testable — build a tree, lay it out, dispatch synthetic input, and assert widget state or emitted
`ZgPaintCommand`s. See `Zigote.Tests` (layout, reconciler, navigator, focus, hot-path-allocation). Don't reference
`Zigote.Editor` from tests — it initialises the native engine.
