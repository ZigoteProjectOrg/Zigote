# Migrating from Jetpack Compose / Compose Multiplatform

Compose and Zigote share a philosophy — declarative trees, no XML, state-driven UI, a self-rendered
widget set on a GPU canvas — and disagree completely on mechanism.

Compose is **immediate mode with memoization**: your `@Composable` functions re-execute on state
change, the runtime slot-table diffs the result and applies the delta, and `remember` is what keeps
anything alive across those re-executions.

Zigote is **retained mode**: widgets are ordinary long-lived objects, your build function runs once,
and you mutate the objects afterwards. There is no recomposition, no slot table, no `remember`.

Read [`concepts.md`](concepts.md) first. The mental adjustment is bigger coming from Compose than
from Flutter, because Compose leans on recomposition much harder.

---

## The single most important translation

**`remember { … }` becomes a field.**

Everything you would wrap in `remember` — because it must survive recomposition — is simply a field
on your widget class, because there is no recomposition to survive.

```kotlin
// Compose
@Composable
fun Counter() {
    var count by remember { mutableStateOf(0) }
    Column {
        Text("Count: $count")
        Button(onClick = { count++ }) { Text("Increment") }
    }
}
```

```csharp
// Zigote
public sealed class Counter : ComposedWidget
{
    private readonly Signal<int> _count = new(0);          // ← the `remember { mutableStateOf(0) }`

    protected override Widget Build(BuildContext ctx) => new Column(
        spacing: Spacing.Sm,
        children:
        [
            new Watch(() => new Label($"Count: {_count.Value}")),   // ← the recomposition scope
            new Button("Increment", () => _count.Value++),
        ]);
}
```

Two mappings to internalize:

| Compose | Zigote |
|---|---|
| `remember { … }` | a `readonly` field |
| `mutableStateOf(x)` | `new Signal<T>(x)` |
| `by` delegate (`var count by …`) | `.Value` |
| **an implicit recomposition scope** | **an explicit `Watch`** |

That last row is the one that bites. Compose infers the smallest scope that reads a state object.
Zigote does not infer anything: **you draw the scope yourself with `Watch`.** A signal read outside a
`Watch` is read once, at build time, and never again.

Draw them tight. A `Watch` around the page rebuilds the page's subtree per keystroke; a `Watch`
around the count rebuilds one label. This is the same discipline as keeping composables small so
recomposition stays narrow — just made explicit.

---

## API map

### Composition and state

| Compose | Zigote | Notes |
|---|---|---|
| `@Composable fun Foo()` | `class Foo : ComposedWidget` + `Build` | Build runs once |
| `remember { x }` | a field | |
| `remember(key) { x }` | a field + recompute when the key changes | No implicit key invalidation |
| `rememberSaveable` | *(nothing built in)* | Persist via `Zigote.Preferences` / `Zigote.Persistence` |
| `mutableStateOf` | `Signal<T>` | Thread-safe writes; marshalled to the UI thread |
| `mutableStateListOf` | `Signal<ImmutableArray<T>>` | Write a new array; the signal compares by reference |
| `derivedStateOf { … }` | `Computed.From(() => …)` | Auto-tracks, caches, no key list |
| `snapshotFlow { … }.collect` | `OwnEffect(() => …)` | Auto-tracks its reads |
| implicit recomposition scope | `new Watch(() => …)` | **Explicit.** |
| `LaunchedEffect(Unit) { … }` | `OnMount()` + `Background.RunAsync` | |
| `LaunchedEffect(key) { … }` | `OwnEffect(() => { … })` reading the key signal | |
| `DisposableEffect { … onDispose { } }` | `OwnEffect(() => { …; return cleanup; })` | Cleanup runs before each re-run and on dispose |
| `rememberCoroutineScope()` | the bloc's `Restart()` / `Track()`, or `Background` | |
| `CompositionLocal` / `ProvidableCompositionLocal` | `InheritedWidget` | `ctx.DependOn<T>()` |
| `LocalDensity` / `LocalConfiguration` | `MediaQuery.Of(ctx)` | `.DevicePixelRatio`, `.Size`, `.SizeClass` |
| `MaterialTheme.colorScheme` | `Theme.Of(ctx)` → `ThemeData` | |
| `key(id) { … }` in a list | `Key` / `ValueKey<T>` on the child | Only meaningful across `SetChildren` |
| `StateFlow` + `collectAsState()` | `Signal<T>` + `Watch` | |
| `Flow` operators | `Zigote.Reactive.R3` bridges `Signal<T>` ↔ R3 `Observable<T>` | |
| ViewModel + `viewModelScope` | `Zigote.Bloc`'s `Bloc<TEvent, TState>` | Owns its concurrency and disposal |
| Hilt / Koin | constructor injection at the composition root | There is no container |

### Layout and modifiers

Compose's `Modifier` chain has no counterpart. Zigote follows Flutter: **wrapper widgets**.

| Compose | Zigote |
|---|---|
| `Modifier.padding(8.dp)` | `new Padding(EdgeInsets.All(8), child)` |
| `Modifier.fillMaxSize()` | `new SizedBox(width: …, height: …)`, or `Expanded` inside a flex |
| `Modifier.size(40.dp)` | `new SizedBox(40, 40, child)` |
| `Modifier.background(color)` | `new ColoredBox(color, child)`, or `Container(color:)` |
| `Modifier.clip(RoundedCornerShape(8.dp))` | `new ClipRRect(8, child)` (`ClipRect` for square corners) |
| `Modifier.clickable { }` | `new GestureDetector(child) { OnTap = … }`, or `Pressable` |
| `Modifier.alpha(f)` | `new Opacity(0.5, child)` |
| `Modifier.graphicsLayer { … }` | `new Transform(offset, child)` |
| `Modifier.weight(1f)` | `new Expanded(child, flex: 1)` |
| `Modifier.align(…)` | `new Align(Alignment.Center, child)` |
| `Modifier.aspectRatio(r)` | `new AspectRatio(r, child)` |
| `Modifier.verticalScroll(state)` | `new SingleChildScrollView(child)` |
| `Modifier.safeDrawingPadding()` | `new SafeArea(child)` |

Nesting a few wrappers is idiomatic here and costs no more than a modifier chain does — both are one
object per stage. If a wrapper stack gets deep enough to hurt readability, factor it into a
`ComposedWidget` or a static helper method.

### Containers and controls

| Compose | Zigote |
|---|---|
| `Column` / `Row` | `Column` / `Row` (`mainAxisAlignment:`, `crossAxisAlignment:`, `spacing:`) |
| `Box` | `Stack` |
| `Spacer(Modifier.weight(1f))` | `new Spacer()` |
| `LazyColumn` | `ListView.Builder` — see [Lists](#lazycolumn-and-lazyverticalgrid) |
| `LazyVerticalGrid` | `GridView.Builder`, `ResponsiveGrid` |
| `Text` | `Label` (or `Text`, an alias taking a `TextStyle`) |
| `Button` / `OutlinedButton` / `TextButton` | `Button`, or Material's `ElevatedButton` / `OutlinedButton` / `TextButton` |
| `Icon` / `IconButton` | `Icon` / `IconButton` |
| `TextField` / `OutlinedTextField` | `TextField` (`Zigote.UI.Material`) |
| `Checkbox` / `RadioButton` / `Switch` / `Slider` | same names |
| `Scaffold` / `TopAppBar` / `FAB` | `Scaffold` / `AppBar` / `FloatingActionButton` |
| `ModalBottomSheet` | `AdwBottomSheet` (Adwaita), or a `Dialog` / overlay |
| `AlertDialog` | `Dialog.Alert` / `Dialog.Confirm`, or `new Dialog(content).Show()` |
| `Snackbar` / `SnackbarHostState` | `app.ShowSnackbar(message, …)` |
| `TabRow` / `HorizontalPager` | `TabBar` / `TabBarView` |
| `NavHost` / `navController` | `Navigator` — `ctx.Push(page)`, `ctx.Pop(result)`, `Routes`, `Pages` |
| `AnimatedVisibility` | `AnimatedOpacity`, `AnimatedSize`, `FadeTransition` |
| `AnimatedContent` / `Crossfade` | `AnimatedSwitcher` |
| `animateFloatAsState` | `TweenAnimationBuilder<float>`, or an `AnimationController` |
| `rememberInfiniteTransition` | `controller.Repeat(reverse: true)` |
| `BoxWithConstraints` | `LayoutBuilder` |
| `WindowSizeClass` | `MediaQuery.Of(ctx).SizeClass` (`Compact` / `Medium` / `Expanded`), `AdaptiveBuilder` |

---

## Effects, ported

`LaunchedEffect(Unit)` — run once when the widget enters:

```csharp
public sealed class ProfilePage : ComposedWidget
{
    private readonly Label _name = new("…");

    protected override void OnMount()
    {
        _env.Background.RunAsync(async ct =>
        {
            var user = await _api.LoadAsync(UserId, ct);
            _env.Background.Post(() =>                    // back on the UI thread
            {
                _name.Text = user.Name;
                MarkNeedsLayout();
            });
        });
    }

    protected override Widget Build(BuildContext ctx) => _name;
}
```

`LaunchedEffect(key)` — re-run when a key changes. Read the key inside an `OwnEffect`; auto-tracking
does the rest:

```csharp
protected override void OnMount()
{
    // Re-runs whenever _userId changes. No key list — the effect tracks what it reads.
    OwnEffect(() =>
    {
        var id = _userId.Value;
        var token = _reload.Restart();          // latest-wins: cancels the previous load
        _ = LoadAsync(id, token);
    });
}
```

`DisposableEffect` — cleanup on re-run and on dispose:

```csharp
OwnEffect(() =>
{
    var sub = _service.Subscribe(_channel.Value, OnMessage);
    return () => sub.Dispose();      // runs before each re-run, and once at disposal
});
```

Always use `OwnEffect`, never `new Effect(...)`. Signals hold observers strongly, so a bare effect
outlives the widget and keeps firing against a detached tree. And if you override `Dispose`, call
`base.Dispose()` — that is what releases them.

---

## `LazyColumn` and `LazyVerticalGrid`

`ListView.Builder` is `LazyColumn`: rows are built when they scroll into the window and destroyed
when they leave, so measure, layout, paint **and** construction are all O(viewport).

```csharp
var list = ListView.Builder(tracks.Length, i => RowFor(tracks[i]), itemExtent: 36);
```

`LazyVerticalGrid` maps to `GridView.Builder(crossAxisCount, itemCount, i => …)` — one grid row of
cells built at a time, self-scrolling.

The builder lambda replaces `items(list) { … }`, and carries the same rule Compose's does: an item
that leaves the window is destroyed, so its widget-held state (hover, focus, a nested scroll offset)
goes with it. Keep item state in your model — there is no `rememberSaveable` here.

`SetItems` is the eager alternative: every row materialized and kept alive, at roughly 8 µs each.

Under a few hundred rows this is invisible. Above that, if you need eager rows, fill across frames —
`Background.Slice` owns the per-frame budget and supersedes any fill already running for the same
key:

```csharp
private readonly ListView _list = new();   // hoisted — survives every rebuild

private void ShowTracks(ImmutableArray<Track> tracks)
{
    _list.SetItems([], keepScroll: true);

    if (tracks.Length <= 400)
    {
        foreach (var t in tracks) _list.AddItem(RowFor(t));
        return;
    }

    // Keyed by the list itself, so a query changing on every keystroke cancels its own
    // half-built predecessor instead of racing it.
    _env.Background.Slice(_list, tracks.Length, i => _list.AddItem(RowFor(tracks[i])));
}
```

Variable-height rows: set `HeightOf` and the list keeps a prefix-sum table and binary-searches the
visible window.

```csharp
_list.HeightOf = i => rows[i].IsHeader ? 48f : 36f;
```

---

## Gotchas, in the order you will meet them

**There is no recomposition, so nothing re-reads your state.** A `Text("Count: $count")` in Compose
re-executes. `new Label($"Count: {_count.Value}")` here evaluates its string once. Put it in a
`Watch`, or mutate `_label.Text`.

**`Watch` scopes are not inferred.** Compose finds the narrowest scope for you. Here, forgetting a
`Watch` produces a UI that silently never updates — the most common first-day bug.

**A `Watch` swap destroys its subtree.** Unlike a recomposition, which preserves `remember`ed values,
a `Watch` replacing its child tears down every widget inside: scroll offsets, focus, animations. Hoist
stateful children to fields and mutate them inside the `Watch`, returning the same instance.

**Widget instances are expensive to replace, cheap to mutate.** The inverse of Compose's advice.
Recreating a widget loses its hover, focus, scroll and in-flight animation.

**No `@Stable` / `@Immutable` / skipping.** There is no skippability analysis because there is no
re-execution. Equality of your data classes affects `Signal`/`Computed` change detection only.

**Mutating state does not rebuild.** Write to the retained child and call `MarkNeedsLayout`.
`MarkNeedsBuild` re-runs `Build` — reserve it for when the tree's *shape* changes.

**`Home` must be set before `Run()`.** `ZigoteApp.Run` captures `Home` into the root `Navigator`
before calling `OnInit`, so a tree built in `OnInit` is never mounted. Set it in the constructor.

**`dp` does not exist as a type.** Sizes are `float` logical pixels. Use the `Spacing` /
`ControlMetrics` / `Radii` token classes rather than literals; `MediaQuery.Of(ctx).DevicePixelRatio`
is there when you need the real ratio.

---

## What you gain

- **A single native binary.** NativeAOT publish — no JVM, no Skiko bundle, no Gradle. Cold start in
  milliseconds, ~10–20 MB.
- **Real desktop Linux.** `Zigote.UI.Adwaita` follows system light/dark and accent live, so a GNOME
  app looks like a GNOME app rather than Material-on-Linux.
- **A 3D engine sharing the frame loop.** Scenes, ECS, physics, shader graphs and widgets in one
  process. No `AndroidView`-style seam.
- **Predictable performance.** No recomposition storms, no skippability puzzles, no
  `@Stable` annotations. If the UI is slow, the profiler points at layout or paint, both of which are
  your code.
- **The .NET ecosystem**, and C# + F# on the same widget API.

## What you lose

- **Accessibility.** The semantics tree is built; no platform bridge ships. TalkBack / screen readers
  see nothing. Compose's accessibility is genuinely good and this is a real regression.
- **Android and iOS maturity.** Compose is mobile-first; Zigote's mobile support is in bring-up. See
  `docs/mobile-port.md`.
- **Web / wasm.** No target.
- **Kotlin.** Coroutines, structured concurrency, sealed-class exhaustiveness and DSL builders are
  nice things you are trading for C#'s. `Bloc` + `Background` + `record` + pattern matching cover most
  of it, but `suspend` functions have no exact analogue.
- **The Compose tooling.** No `@Preview`, no Layout Inspector, no Compose compiler metrics. You get
  hot reload under `dotnet watch` and a Shift+D widget inspector.
- **Maven Central.** No third-party widget libraries.
