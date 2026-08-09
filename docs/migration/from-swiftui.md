# Migrating from SwiftUI

Of the four toolkits people arrive from, SwiftUI is the closest in *taste* and the furthest in
*mechanism*.

The taste is deliberate. `Zigote.UI`'s design language is flat, minimalist macOS: opaque surfaces
layered by elevation, an accent-tinted palette, an 8-pt spacing grid, soft low-opacity shadows — and
the type ramp is SF's, by name. `Typography.LargeTitle`, `Title1`, `Title2`, `Title3`, `Headline`,
`Body`, `Callout`, `Subheadline`, `Footnote`, `Caption`. `ThemeData.Dark` *is* `ThemeData.MacDark()`.
Your visual instincts port unchanged.

The mechanism does not. SwiftUI is a **value-type description**: `body` is a computed property, your
`View` structs are cheap immutable descriptions, and the framework diffs them against a private
persistent tree, matching by structural identity. Nothing you write in `body` survives — `@State` is
stored *outside* it, keyed by position.

Zigote is **retained mode**: widgets are long-lived reference-type objects, your `Build` runs once,
and you mutate the objects afterwards. There is no diff, no structural identity, no view graph.

Read [`concepts.md`](concepts.md) first. Then the one paragraph below that does most of the work.

---

## The single most important translation

**A `View` struct is a description. A `Widget` is the thing itself.**

In SwiftUI you never hold onto a view — you return a fresh one and let identity do the rest. In
Zigote you *always* hold onto a widget, because that object owns its own hover, focus, scroll
position and in-flight animation. Recreating it throws all of that away.

```swift
// SwiftUI — a new Text value every evaluation, and that is free and correct
struct Counter: View {
    @State private var count = 0
    var body: some View {
        VStack(spacing: 8) {
            Text("Count: \(count)")
            Button("Increment") { count += 1 }
        }
    }
}
```

```csharp
// Zigote
public sealed class Counter : ComposedWidget
{
    private readonly Signal<int> _count = new(0);          // ← @State

    protected override Widget Build(BuildContext ctx) => new Column(
        spacing: Spacing.Sm,
        children:
        [
            new Watch(() => new Label($"Count: {_count.Value}")),   // ← the invalidation scope
            new Button("Increment", () => _count.Value++),
        ]);
}
```

Three mappings to internalize:

| SwiftUI | Zigote |
|---|---|
| `@State private var x = 0` | `private readonly Signal<int> _x = new(0);` |
| `$x` / `x` in `body` | `_x.Value` |
| **implicit body invalidation** | **an explicit `Watch`** |

That last row is the one that bites. SwiftUI recomputes `body` whenever any `@State` it read changes,
and works out the minimum to redraw. Zigote infers nothing: **you draw the invalidation scope
yourself with `Watch`.** A signal read outside a `Watch` is read once, at build time, and never
again — the UI silently never updates. That is the number-one first-day bug.

Scope them tight, the same way you would factor a SwiftUI view down so `body` recomputes narrowly.
Here it is explicit rather than inferred.

---

## API map

### State and data flow

| SwiftUI | Zigote | Notes |
|---|---|---|
| `struct V: View` + `var body` | `class V : ComposedWidget` + `Build` | `Build` runs **once** |
| `@State` | `Signal<T>` field | |
| `@Binding` | pass the `Signal<T>`, or a `(get, set)` pair | Reference type — passing it *is* the binding |
| `$value` projection | pass `_value` itself | No projected-value machinery |
| `@StateObject` / `@ObservedObject` | a `Bloc` held in a field / passed in | Ownership is explicit, not inferred from the wrapper |
| `ObservableObject` + `@Published` | `Bloc<TEvent, TState>`, or a class of `Signal<T>` | |
| `@Observable` macro | a `Signal<T>` per property, or one `Signal<Record>` | |
| `objectWillChange` | `signal.Invalidated` / `signal.Changed` | |
| `@EnvironmentObject` | `InheritedWidget` + `ctx.DependOn<T>()`, or constructor injection | Prefer injection; no runtime crash for a missing object |
| `@Environment(\.colorScheme)` | `Theme.Of(ctx)` | Returns `ThemeData` |
| `@Environment(\.horizontalSizeClass)` | `MediaQuery.Of(ctx).SizeClass` | `Compact` / `Medium` / `Expanded` |
| `EnvironmentValues` / `EnvironmentKey` | subclass `InheritedWidget`, override `UpdateShouldNotify` | |
| `@AppStorage("key")` | `Zigote.Preferences`' `Preference<T>` | It *is* an `IReadableSignal<T>` — drops straight into a `Watch` |
| `@FocusState` | `widget.Focused`, `App.RequestFocus` | |
| Computed property over `@State` | `Computed.From(() => …)` | Auto-tracks and caches |
| Combine `Publisher` / `.sink` | `Signal.Subscribe`, or `Zigote.Reactive.R3` for operators | |
| `.onReceive(publisher)` | `OwnEffect(() => …)` | Auto-tracks its reads |

`@AppStorage`, side by side:

```swift
@AppStorage("showGrid") private var showGrid = true
Toggle("Show grid", isOn: $showGrid)
```

```csharp
public sealed class EditorPreferences(PreferenceStore store) : PreferencesProvider(store, "editor")
{
    public Preference<bool> ShowGrid { get; } = /* Register in ctor */ null!;
    // ShowGrid = Register("showGrid", true);   → key "editor.showGrid", persisted, reactive
}

new Watch(() => new Switch(prefs.ShowGrid.Value, v => prefs.ShowGrid.Value = v))
```

### Layout: modifiers become wrappers

SwiftUI's modifier chain has no counterpart. Zigote follows Flutter: **wrapper widgets**, outermost
last-applied.

| SwiftUI | Zigote |
|---|---|
| `VStack` / `HStack` | `Column` / `Row` (`mainAxisAlignment:`, `crossAxisAlignment:`, `spacing:`) |
| `ZStack` | `Stack` (+ `Positioned` for absolute placement) |
| `Spacer()` | `new Spacer()` |
| `.padding(8)` | `new Padding(EdgeInsets.All(8), child)` |
| `.padding(.horizontal, 16)` | `new Padding(EdgeInsets.Symmetric(horizontal: 16), child)` |
| `.frame(width: 40, height: 40)` | `new SizedBox(40, 40, child)` |
| `.frame(maxWidth: .infinity)` | `new Expanded(child)` inside a flex |
| `.background(Color.blue)` | `new ColoredBox(Colors.Blue, child)`, or `Container(color:)` |
| `.cornerRadius(8)` / `.clipShape(RoundedRectangle(…))` | `new ClipRRect(8, child)`, or `Container(decoration: new BoxDecoration(borderRadius: BorderRadius.Circular(8)))` |
| `.overlay { }` | `new Stack(children: [child, overlay])` |
| `.border(…)` / `.stroke(…)` | `Container(decoration: new BoxDecoration(border: Border.All(color)))` |
| `.opacity(0.5)` | `new Opacity(0.5, child)` |
| `.offset(x:y:)` | `new Transform(new Offset(x, y), child)` |
| `.aspectRatio(_, contentMode:)` | `new AspectRatio(ratio, child)` |
| `.onTapGesture { }` | `new GestureDetector(child) { OnTap = … }`, or `Pressable` |
| `.onHover { }` | `GestureDetector.OnHoverEnter` / `OnHoverExit` |
| `.contextMenu { }` | `ContextMenu`, or override `Widget.OnRightClick` |
| `.help("…")` | `Tooltip`, or override `Widget.TooltipText` |
| `.hidden()` | `new Opacity(0, child)` |
| `if cond { A } else { B }` in `body` | the same C# ternary — **inside a `Watch`** |
| `GeometryReader` | `LayoutBuilder` |
| `.safeAreaInset` / `.ignoresSafeArea` | `SafeArea`, `MediaQuery.Of(ctx).Padding` |
| `.fixedSize()` | `MainAxisSize.Min`, `ConstrainedBox` |

Nesting a few wrappers reads fine and costs the same as a modifier chain — both are one object per
stage. When a stack gets deep, factor it into a `ComposedWidget` (your `ViewModifier`) or a static
helper method (your `.buttonStyle`).

### Controls and containers

| SwiftUI | Zigote |
|---|---|
| `Text` | `Label` (or `Text`, an alias taking a `TextStyle`) |
| `.font(.title2)` | `new Label(s) { Style = … }`, or `new Text(s, style: Typography.Title2)` |
| `Button` | `Button`, or Material's `ElevatedButton` / `OutlinedButton` / `TextButton` |
| `Image` / `AsyncImage` | `Image` / `AsyncImage` |
| `Label("Title", systemImage:)` | `Row` of `Icon` + `Label`; icons are `MaterialIcons.*` string constants |
| `TextField` / `SecureField` | `TextField(controller:, obscureText:)` (`Zigote.UI.Material`) |
| `Toggle` | `Switch` |
| `Slider` / `Stepper` | `Slider` / `Stepper`, `NumberInput` |
| `Picker` (menu) | `Dropdown<T>` |
| `Picker` (`.segmented`) | `SegmentedControl` |
| `ProgressView` (determinate) | `ProgressBar` |
| `ProgressView` (indeterminate) | `Spinner` |
| `Divider` | `Divider` |
| `ScrollView` | `SingleChildScrollView` / `ScrollView` |
| `List` / `ForEach` | `ListView.Builder` / `SetItems` — see [Lists](#list-and-foreach) |
| `LazyVGrid` | `GridView.Builder` (lazy), `GridView.Count`, `ResponsiveGrid` |
| `Form` / `Section` | `AdwPreferencesPage` / `AdwPreferencesGroup` / `AdwActionRow` (Adwaita), or `Column` + `Card` |
| `.searchable` | `SearchField`, `AutoSuggestField` |
| `TabView` | `TabBar` + `TabBarView` |
| `NavigationStack` / `NavigationLink` | `Navigator` — `await ctx.Push(page)`, `ctx.Pop(result)` |
| `NavigationSplitView` | `NavigationSplitView` (Material), `AdwNavigationSplitView` (Adwaita) |
| `.sheet` / `.fullScreenCover` | `AdwBottomSheet`, or `ctx.Push(page)` |
| `.alert` / `.confirmationDialog` | `Dialog.Alert` / `Dialog.Confirm`, `new Dialog(content).Show()` |
| `.popover` | `Popover` |
| `.toolbar` | `Toolbar`, `AppBar`, `AdwHeaderBar` |
| `.commands { CommandMenu … }` | one `AppMenu` model → native `NSMenu` on macOS, in-window `MenuBar` elsewhere |
| `.draggable` / `.dropDestination` | `Draggable<T>` / `DragTarget<T>` (also OS file/text drops) |
| `.keyboardShortcut` | `App.Keymap.Bind(action, chord)` + `App.OnShortcut` |
| `Canvas` / `Shape` / `Path` | subclass `Widget` (or `LeafWidget`) and override `Paint(PaintList)` |
| `App` / `Scene` / `WindowGroup` | `ZigoteApp` (`AdwaitaApp` / `MaterialApp`); `AdwaitaApp.OpenWindow(...)` for a second window |

### Lifecycle and effects

| SwiftUI | Zigote |
|---|---|
| `.onAppear { }` | `Widget.OnMount()` |
| `.onDisappear { }` | `Widget.OnUnmount()` — or just `Own(...)` it in `OnMount` |
| `.task { }` | `OnMount()` + `Background.RunAsync(async ct => …)` |
| `.task(id:) { }` | `OwnEffect(() => { var id = _id.Value; … })` — auto-tracks the id |
| `.onChange(of: x) { }` | `OwnEffect(() => { _ = _x.Value; … })`, or `_x.Changed += …` |
| structured cancellation on disappear | `Bloc.Restart()` / `Bloc.Track()` / the state's `OwnEffect` |
| `@MainActor` | there is one UI thread; `App.Post(action)` hops onto it |

`.task(id:)` and `.onChange`, ported:

```csharp
protected override void OnMount()
{
    // Re-runs whenever _userId changes. No dependency list — the effect tracks what it reads.
    OwnEffect(() =>
    {
        var id = _userId.Value;
        var token = _reload.Restart();       // latest-wins: cancels the in-flight load
        _ = LoadAsync(id, token);
    });

    // .onDisappear cleanup, per run: return a thunk.
    OwnEffect(() =>
    {
        var sub = _service.Subscribe(_channel.Value, OnMessage);
        return () => sub.Dispose();
    });
}
```

Always use `OwnEffect`, never a bare `new Effect(...)`: signals hold observers strongly, so a bare
effect outlives the widget and keeps firing against a detached tree.

### Animation

| SwiftUI | Zigote |
|---|---|
| `withAnimation { state = x }` | assign to an `Animated*` widget's property — the animation is the widget |
| `.animation(_, value:)` | `AnimatedOpacity`, `AnimatedAlign`, `AnimatedPadding`, `AnimatedSize`, `AnimatedContainer` |
| `.transition(.opacity)` on insert/remove | `AnimatedSwitcher` |
| `.animation` on a computed value | `TweenAnimationBuilder<T>` |
| `Animatable` / `animatableData` | `AnimationController` + `MarkNeedsPaint` in `OnTick` |
| `TimelineView` / repeating | `controller.Repeat(reverse: true)` |
| `matchedGeometryEffect` | *(no equivalent)* — hoist the shared widget and animate its position |
| `.transaction` / `Animation.spring` | `Curves.*` on the controller; no spring solver ships |

```csharp
// The "withAnimation" equivalent: the widget owns the animation, you just set the value.
_panel.Child = expanded ? _details : SizedBox.Shrink();   // AnimatedSize animates the height
new AnimatedOpacity(visible ? 1f : 0f, _badge, duration: 0.2f)
```

Entrances and one-shots have a fluent form:

```csharp
new Card { Child = content }.Animate().Fade(300.ms).Move(delay: 100.ms)
```

---

## `List` and `ForEach`

`ListView` virtualizes **measure, layout and paint** to the viewport, so scrolling stays O(viewport)
at any row count. `ListView.Builder` is the `List`/`ForEach` analogue — it virtualizes *construction*
too, building a row when it enters the window and destroying it when it leaves:

```csharp
var list = ListView.Builder(tracks.Length, i => RowFor(tracks[i]), itemExtent: 36);
```

`LazyVGrid` maps to `GridView.Builder(crossAxisCount, itemCount, i => …)`, which builds one grid row
of cells at a time and scrolls itself.

Laziness costs the same here as in SwiftUI: a row that scrolls out is gone, and so is anything it
held (hover, focus, a nested scroll offset). Keep row state in your model.

`SetItems` is the eager alternative — every row materialized and kept alive, at roughly 8 µs each.
Under a few hundred rows the difference is invisible. Above that, if you need eager rows, fill across
frames — `Background.Slice` owns the per-frame budget and supersedes any fill already running for the
same key:

```csharp
private readonly ListView _list = new();     // hoisted: never construct this inside a Watch

private void Show(ImmutableArray<Track> tracks)
{
    _list.SetItems([], keepScroll: true);

    if (tracks.Length <= 400)
    {
        foreach (var t in tracks) _list.AddItem(RowFor(t));
        return;
    }

    // The list is the key, so a query changing on every keystroke cancels its own
    // half-built predecessor rather than interleaving with it.
    _env.Background.Slice(_list, tracks.Length, i => _list.AddItem(RowFor(tracks[i])));
}
```

Variable-height rows — the analogue of a `List` with mixed cell heights:

```csharp
_list.ItemHeight = 36f;                                    // uniform default
_list.HeightOf   = i => rows[i].IsHeader ? 48f : 36f;      // prefix-sum + binary search
```

`ForEach(items, id: \.id)` maps to `Key` / `ValueKey<T>` on the children, and matters in exactly one
place: `MultiChildWidget.SetChildren`, where the reconciler reuses retained instances across
insert / remove / reorder instead of rebuilding them.

```csharp
row.SetChildren(items.Select(i => new RowWidget(i) { Key = new ValueKey<int>(i.Id) }));
```

Without keys it matches positionally — fine for a static list, wrong for anything the user reorders.

---

## Custom drawing

There is no `Canvas`, `Shape` or `Path` type. Custom drawing means a widget that implements the three
phases itself — closer to `UIView.draw(_:)` than to SwiftUI's `Canvas`, and the same three methods
you would override for a custom `Layout`:

```csharp
public sealed class Sparkline(IReadOnlyList<float> samples) : LeafWidget
{
    private Size _size;

    public override Size Measure(Constraints c) => _size = c.Constrain(new Size(120, 32));

    public override void Layout(Offset origin) =>
        Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    public override void Paint(PaintList paint)
    {
        if (samples.Count < 2) return;
        var (min, max) = (samples.Min(), samples.Max());
        var span = MathF.Max(max - min, float.Epsilon);
        var step = Bounds.Width / (samples.Count - 1);

        Span<Offset> points = stackalloc Offset[samples.Count];
        for (var i = 0; i < samples.Count; i++)
            points[i] = new Offset(
                Bounds.X + i * step,
                Bounds.Y + Bounds.Height * (1f - (samples[i] - min) / span));

        paint.AddPolygon(points, Colors.Blue);
    }
}
```

`PaintList` gives you `AddRect`, `AddBorder`, `AddText`, `AddImage`, `AddPolygon`, `AddBezier`,
`AddShadow`, clip and opacity push/pop, and `AddShaderEffect` for a custom WGSL pass. Paint runs on
the hot path — it emits flat structs into a reused buffer, so keep allocations out of it
(`stackalloc` above is not an affectation).

Most widgets should *not* do this. Compose from the layout kernel + `DecoratedBox` + `Pressable`
instead; hand-written measure/layout/paint is for genuine primitives.

---

## Gotchas, in the order you will meet them

**Nothing re-reads your state.** `Text("Count: \(count)")` re-evaluates in SwiftUI. `new Label($"Count:
{_count.Value}")` here evaluates its string once, at first measure. Put it in a `Watch`, or mutate
`_label.Text`.

**`Watch` scopes are not inferred.** SwiftUI finds the invalidation boundary for you; here, a missing
`Watch` produces a UI that silently never updates.

**A `Watch` swap destroys its subtree.** Unlike a `body` recomputation — which preserves `@State`
because state lives outside `body` — a `Watch` replacing its child tears down every widget inside it:
scroll offsets, focus, animations. Hoist stateful children into fields, mutate them inside the
`Watch`, and return the same instance.

**Widgets are reference types with identity.** The SwiftUI habit of returning freshly-constructed
views is exactly wrong here. `new Button(...)` in a hot path is a new object with no hover, no focus,
no animation. Hold it in a field and mutate it.

**`Build` locals are frozen.** A `let label = "Count: \(count)"` in `body` re-evaluates. The C#
equivalent inside `Build` does not.

**Mutating state does not re-run `Build`.** Write to the retained child and call `MarkNeedsLayout`.
`MarkNeedsBuild` re-runs `Build` — reserve it for when the tree's *shape* changes, not for every
value change.

**Setting a property on a custom widget is not enough.** Call `MarkNeedsPaint()` (visuals only) or
`MarkNeedsLayout()` (size may have changed), or nothing redraws.

**`Home` must be set before `Run()`.** `ZigoteApp.Run` captures `Home` into the root `Navigator`
*before* calling `OnInit`, so a tree built in `OnInit` is never mounted and the window comes up blank.
Set `Home` in the constructor; build its contents lazily on the first `Build` if they need services
`OnInit` creates.

**No `.id()` and no structural identity.** `Key` exists but only affects `SetChildren`. There is no
mechanism that resets a widget's state because its position in the tree changed — you control
lifetime directly.

**Points are `float` logical pixels.** No `CGFloat`, no `.dp`. Use the `Spacing` / `Radii` /
`ControlMetrics` / `Typography` token classes rather than literals;
`MediaQuery.Of(ctx).DevicePixelRatio` is there when you need the real ratio.

---

## What you gain

- **It leaves Apple platforms.** The same tree runs on macOS, Linux and Windows, and looks *native*
  on each — `Zigote.UI.Adwaita` follows the system Adwaita light/dark and accent live on GNOME. This
  is the whole reason to make the trip.
- **The macOS look, portably.** The SF type ramp, 8-pt grid and elevation model are the base design
  language, not a theme you fight.
- **A 3D engine sharing the frame loop.** Scenes, ECS, physics and shader graphs in-process with the
  widgets — no `SceneKit`/`RealityKit` bridge, no `UIViewRepresentable` seam.
- **The .NET ecosystem**, and C# + F# on the same widget API.
- **Predictable, inspectable performance.** No `body` recomputation storms, no opaque diff. Zero
  allocation on the steady paint path, and a widget inspector on Shift+D.
- **Headless testability.** Build a tree, measure, lay out, dispatch synthetic input, assert — no
  simulator, no `XCUITest`, no snapshot harness.

## What you lose

- **Accessibility.** VoiceOver support is one of SwiftUI's genuine strengths, largely free. Zigote
  builds a complete semantics tree but ships no platform bridge — screen readers see nothing. If you
  are under an accessibility mandate, this is a blocker today, not a rough edge.
- **iOS.** Mobile is in bring-up (touch, lifecycle, safe area and native builds work; see
  `docs/mobile-port.md`). SwiftUI on iPhone is not something this replaces.
- **Deep Apple integration.** No SwiftData, no CloudKit, no WidgetKit, no App Intents, no Catalyst.
- **The declarative sugar.** Modifier chains, `@Binding` projection, `matchedGeometryEffect`, spring
  animations, and previews all have to be written out longhand or done without.
- **Xcode previews.** You get C# hot reload instead: edit a `Build()` under `dotnet watch` and the
  live UI updates with instances and state preserved. Constructor bodies, field initializers and
  `OnMount` edits still need a restart.
- **Swift.** Value semantics, `some View` opaque types, result builders and structured concurrency
  are being traded for C#'s records, pattern matching and `Task`. `Bloc` + `Background` cover most of
  the concurrency ground, but result builders have no analogue — trees are built with collection
  expressions and object initializers.
