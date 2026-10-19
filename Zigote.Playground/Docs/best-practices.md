# Best Practices

The rules that keep Zigote apps fast, correct and maintainable. Most follow from one fact: **the widget tree is retained**. Instances live for the life of the screen, and you change them rather than rebuild them.

## Widgets

- **Never recreate a widget you can mutate.** A new instance loses hover, focus, scroll position and running animations, and allocates. Keep stateful children in fields.
- **Compose before you hand-write.** Layout widgets, `DecoratedBox` and `Pressable` cover nearly everything. Write `Measure`, `Layout` and `Paint` only for real primitives such as canvases, charts and editors.
- **Build child widgets once, in the constructor.** Wire per-mount things (effects, tickers, subscriptions) in `OnMount`. `Build` assembles the tree and reads the theme; it is not a lifecycle hook.
- **Pick the cheapest invalidation.** Use `MarkNeedsPaint` for visual-only changes, `MarkNeedsLayout` when size or text changed, and `MarkNeedsBuild` only when the tree's *shape* must change.
- **Key children in lists you replace** with `SetChildren`, so reordering keeps each row's state.
- **Read inherited data in `Build` when the widget should follow it.** The `context` a widget is given stays valid in its handlers (`context.Push(...)` from an `onPressed` is fine), but only a lookup made during a build registers a dependency. A theme read in a handler gets the current theme and won't rebuild anything when it changes. Never keep `BuildContext.Current`; it answers only during the layout walk.

```csharp
// ✗ A new label on every click: allocation, layout churn, lost state.
new AdwButton(label: "Go", onPressed: () => _slot.Child = new Label($"{++_n}"));

// ✓ One label, mutated.
new AdwButton(label: "Go", onPressed: () => { _label.Text = $"{++_n}"; _label.MarkNeedsLayout(); });
```

## State

- **Application state lives in signals** (or a `Bloc`); **interaction state lives on widgets.** Don't mirror hover or scroll position into signals.
- **Scope `Watch` tightly.** Wrap the label that changes, not the page around it.
- **Hoist stateful widgets out of `Watch` builders.** Return the same `ListView` or `AdwEntry` instance and mutate it.
- **Derive with `Computed`, don't copy.** Two signals holding related data drift apart; a computed can't.
- **Store immutable values** (records, immutable collections) and replace them; never mutate a list held in a signal in place, because equality won't see the change.
- **Value types in signals implement `IEquatable<T>`.** Records do automatically. Without it, every write boxes, and unchanged writes still notify.
- **Use `Peek()` to read without subscribing**, for example inside event handlers that only need the current value.

## Subscriptions and lifetime

- **Own everything you start**: `OwnEffect`, `Own(disposable)`, `CreateTicker`. Signals hold observers strongly, so a bare `new Effect(...)` outlives its widget and keeps running.
- **Give each feature a `Background.Child` scope** and dispose it with the feature.
- **Release textures** you load with `LoadTexture*`: handles are caller-owned. Watch `gpu.textures` in DevTools; a count that only climbs is a leak.

## Threading and scheduling

- **Never block the UI thread**: no `.Result`, `.Wait()`, `Thread.Sleep` or synchronous I/O in handlers or `Build`.
- **Background threads write signals and nothing else.** To touch widgets, use `App.Post` or an `EffectAffinity.Deferred` effect.
- **Use `Latest` for anything typed or dragged** (search, filters, previews) so only the newest request wins.
- **`Slice` big UI-thread work**, and use `ListView.Builder` for long lists instead of materialising every row.
- **Batch related writes** with `Reactive.Batch`.
- **Don't poll.** An always-running ticker keeps the app awake. A Zigote app should use no CPU when nothing is happening; check that it does.

## Performance

- **Measure before optimising.** Use the DevTools Performance panel, the `stats` inspect verb and `ZIGOTE_CONTINUOUS=1` for real frame timings. Intuition about where time goes is usually wrong.
- **Watch the idle numbers.** On the Reactive panel, `reactive.runs` or `ui.watch_rebuilds` ticking while nothing changes points to a computed rebuilding a collection every run, an effect writing a signal it reads, or a value type without equality.
- **Avoid allocation in `Paint`.** It runs every frame something is dirty. Cache strings, arrays and layouts in fields; `PaintList` already caches UTF-8 and glyph runs.
- **Paint-time effects are cheap; layout is not.** Animate with `Opacity`, `Transform`, `FadeTransition` and `SlideTransition` rather than changing sizes every frame.
- **Cap image sizes**: pass `maxDim` when loading thumbnails, since a 4K texture per tile adds up fast.

## Animation

- **Implicit first, explicit when you need the timeline**, springs for anything the user can interrupt.
- **Keep implicit animation widgets retained**, so they animate from their current value.
- **150–300 ms, ease out on enter, ease in on exit.**
- **Honour reduce-motion**: `AnimationController.ReduceMotion`.

## Accessibility

- **Every interactive element has a label.** Icon-only buttons need a tooltip and a semantics label. Use the `semantics` inspect verb to see what assistive technology sees.
- **Everything works from the keyboard**: Tab order, Enter/Space activation, Esc to dismiss. Custom controls return `true` from `Focusable` and draw a focus ring.
- **Use theme colours**, not literals, so contrast holds in both light and dark mode.
- **Don't encode meaning in colour alone**; pair it with an icon or text.

## Internationalization

- **No user-visible string literals in widgets**: use `context.Tr`.
- **Whole sentences with placeholders**, never concatenated fragments; plural and select forms instead of `count == 1`.
- **Format numbers, currency and dates through the locale** (`context.FormatCurrency`, …), never `ToString()`.
- **Directional insets** (`EdgeInsetsDirectional`), and test in an RTL locale early.

## Architecture

- **Constructor injection at the composition root.** No service locator and no container; pass a record of services down.
- **Keep the domain free of UI.** Repositories and business rules shouldn't reference signals or widgets.
- **One direction of data flow**: input → event → logic → state → `Watch` → widgets.
- **Business logic in a `Bloc`** once a screen has real behaviour: ordered events, cancellation via `Restart()`, testable without a window.
- **Game code lives in the game project**, never in the engine.

## Testing

- **Test widgets headlessly.** Build a tree, lay it out, dispatch synthetic input, and assert on the paint commands. No window is needed.
- **Use `Background.Manual()` + `Drain()`** so assertions about background work are deterministic, never `Thread.Sleep`.
- **Drive the real app** through the inspect protocol or the MCP server for end-to-end checks: launch, tap by label, screenshot.
- **Self-test your docs and samples**, as this playground does with `--self-test`.

## Native

- **Go through `ZigoteEngine`**, never the generated `NativeEngine` bindings.
- **Never hand-write P/Invokes.** Add the `export fn` in Zig and let the generator emit the binding.
- **Restart after native changes.** Hot reload covers C#, not a rebuilt engine library or shader.
