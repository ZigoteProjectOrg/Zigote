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
// StatelessWidget — pure function of its props; Build() is cached (call Invalidate() to rerun)
class MyCard : StatelessWidget
{
    protected override Widget Build(BuildContext ctx) =>
        new Card(Theme.Of(ctx)) { Child = new Label("Hello") };
}

// StatefulWidget — interactive; SetState mutates retained children in place (no tree rebuild)
class CounterState : WidgetState<Counter>
{
    private readonly Label _label = new("0");
    private int _count;
    public override Widget Build(BuildContext ctx) => new Column
    {
        Children = { _label, new Button("Increment",
            () => SetState(() => _label.Text = (++_count).ToString())) }
    };
}
```

- **`SetState(action)`** relayouts + repaints without re-running `Build`; **`SetStateRebuild`** re-runs
  `Build`. `RequestRebuild()`/`Invalidate()` force a `Build` on the next frame.
- **`InheritedWidget`** (`ThemeProvider`, `MediaQuery`) propagates data down the tree; consumers read it with
  `BuildContext.DependOn<T>()` and rebuild only when it changes.
- **Compose by default** — controls are built from the layout kernel + `DecoratedBox` (background) +
  `Pressable` (interaction). Only genuine primitives (layout containers, animated thumbs, canvases, text editors,
  virtualized lists) hand-write `Measure`/`Layout`/`Paint`.

## What's in the box

| Area                                     | Highlights                                                                                                                                                                                                                                                                                                                                                |
|------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Layout** (`Widgets/Layout/`)           | `Column`/`Row`/`Expanded`/`Stack`/`Wrap`, `Padding`/`Align`/`Center`, `SizedBox`/`ConstrainedBox`, `DecoratedBox`, `Opacity`/`ClipRect`/`Transform`, `ScrollView`/`ListView` (virtualized; `ListView.Builder`/`GridView.Builder` build rows on demand), `SplitPane`, `TreeView<T>`, `ReorderableList`, `NavigationSplitView`                              |
| **Controls** (`Widgets/Controls/`)       | `Label`, `Button`, `Checkbox`, `Radio<T>`, `Switch`, `Slider`, `TextField` (single/multiline + IME), `Dropdown<T>`, `TabBar`/`TabView`, `Card`, `ProgressBar`, `Dialog`, `Snackbar`, `Tooltip`, `ContextMenu`, `Chip`, `Badge`, `NumberInput`, `SegmentedControl`, `SearchField`, `Popover`, `ColorPicker`, `CurveEditor`, `GradientEditor`, `CodeEditor` |
| **Transitions** (`Widgets/Transitions/`) | Explicit (`FadeTransition`/`SlideTransition`/`ScaleTransition`/`AnimatedContainer`) + implicit (`AnimatedOpacity`/`AnimatedAlign`/`AnimatedPadding`/`AnimatedSwitcher`/`TweenAnimationBuilder<T>`) driven by `AnimationController`                                                                                                                        |
| **Animate** (`Widgets/Animate/`)         | flutter_animate-style fluent API: `widget.Animate().Fade(500.ms).Scale(delay: 500.ms)`                                                                                                                                                                                                                                                                    |
| **Navigation** (`Widgets/Navigation/`)   | Navigator 2.0 — `context.Push`/`Pop`, named routes, declarative `Pages` + `OnPopPage`                                                                                                                                                                                                                                                                     |
| **Overlays** (`App`)                     | `PushOverlay`/`PopOverlay`; painted above and hit-tested before Root                                                                                                                                                                                                                                                                                      |
| **DragDrop** (`Widgets/DragDrop/`)       | `Draggable<T>` + `DragTarget<T>` (in-app) and OS-file/text drop                                                                                                                                                                                                                                                                                           |
| **Menu** (`Widgets/Menu/`)               | One `AppMenu` model → native `NSMenu` on macOS, in-window `MenuBar` elsewhere                                                                                                                                                                                                                                                                             |
| **Focus / Semantics**                    | Tab/arrow/Esc traversal (`Focus/`), platform-neutral accessibility tree (`Semantics/`)                                                                                                                                                                                                                                                                    |

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

Edit a widget's `Build()` while the app runs and the live UI updates without a restart — widget instances and
`WidgetState` are preserved, only `Build()` re-runs. Run any
`App`-based app under `dotnet watch`, or use Rider/VS "apply changes". Constructor/field-initializer/
`InitState` edits and native Zig/shader changes still need a full restart.

## Fonts

Cross-platform fonts are bundled here (`Fonts/`) and flattened into `Fonts/` next to every executable that references
`Zigote.UI` — no system-font dependency. **Inter** (default UI), **Iosevka** (code / monospace), **Material Icons**
(glyphs), **Noto Emoji**.

## Design language variants

`Zigote.UI` is the flat-macOS base. Two full alternate design languages layer on top of it (pick one per app): **
`Zigote.UI.Cupertino`** (Cupertino iOS set) and **`Zigote.UI.AppKit`** (AppKit macOS-desktop set + window shell). **
`Zigote.UI.Charts`** adds a composable, declarative charting library, and **`Zigote.UI.Localizations`** adds declarative
i18n.

## Testing

Everything is headless-testable — build a tree, lay it out, dispatch synthetic input, and assert widget state or emitted
`ZgPaintCommand`s. See `Zigote.Tests` (layout, reconciler, navigator, focus, hot-path-allocation). Don't reference
`Zigote.Editor` from tests — it initialises the native engine.
