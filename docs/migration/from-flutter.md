# Migrating from Flutter

Zigote's widget vocabulary was taken from Flutter deliberately: `StatelessWidget`, `StatefulWidget`,
`BuildContext`, `InheritedWidget`, `Column`/`Row`/`Expanded`/`Stack`, `Navigator` with `Push`/`Pop`,
`MediaQuery`, `AnimationController`, `Tween`, implicit and explicit transitions. A Flutter tree ports
across close to line for line, and `Zigote.UI.Material` exists to make that literal.

The vocabulary is the same. **The execution model is not.** Read [`concepts.md`](concepts.md) first —
in particular §1 (`Build` runs once) and §4 (`setState` does not rebuild). Those two differences
account for essentially every problem a Flutter developer hits in their first week.

---

## Hello, world

```dart
// Flutter
void main() => runApp(MaterialApp(
      title: 'Demo',
      theme: ThemeData.dark(),
      home: const HomePage(),
    ));
```

```csharp
// Zigote
new MaterialApp(
    title: "Demo",
    theme: ThemeData.Dark,
    home: new HomePage()
).Run();
```

`MaterialApp` is a named-argument constructor over `ZigoteApp`. It boots the engine, injects
`ThemeProvider` + `MediaQuery`, and wraps `Home` in a root `Navigator`, so `context.Push` / `Pop`,
`Routes`, `InitialRoute`, `OnGenerateRoute` and declarative `Pages` / `OnPopPage` all work the way
you expect. `ZigoteApp` is the same thing with object-initializer syntax.

---

## The counter, ported honestly

```dart
// Flutter
class Counter extends StatefulWidget {
  @override State<Counter> createState() => _CounterState();
}

class _CounterState extends State<Counter> {
  int _count = 0;

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('Counter')),
        body: Center(child: Text('Count: $_count')),
        floatingActionButton: FloatingActionButton(
          onPressed: () => setState(() => _count++),
          child: const Icon(Icons.add),
        ),
      );
}
```

```csharp
// Zigote — the direct translation
public sealed class Counter : StatefulWidget
{
    protected override WidgetState CreateState() => new CounterState();
}

public sealed class CounterState : WidgetState<Counter>
{
    private readonly Label _text = new("Count: 0");   // hoisted: Build runs once
    private int _count;

    public override Widget Build(BuildContext ctx) => new Scaffold(
        appBar: new AppBar(title: new Label("Counter")),
        body: new Center(_text),
        floatingActionButton: new FloatingActionButton(
            child: new Icon(MaterialIcons.Add),
            onPressed: () => SetState(() => _text.Text = $"Count: {++_count}")));
}
```

The interpolated string moved out of `Build` and onto `_text.Text`. That is the whole shape of the
port: **anything that changes has to be reachable from outside `Build`.**

If you would rather not hoist widget references, use a signal and a `Watch` — this is the idiom most
Zigote code actually uses, and it is closer to Riverpod than to `setState`:

```csharp
public sealed class CounterPage : StatelessWidget
{
    private readonly Signal<int> _count = new(0);

    protected override Widget Build(BuildContext ctx) => new Scaffold(
        appBar: new AppBar(title: new Label("Counter")),
        body: new Center(new Watch(() => new Label($"Count: {_count.Value}"))),
        floatingActionButton: new FloatingActionButton(
            child: new Icon(MaterialIcons.Add),
            onPressed: () => _count.Value++));
}
```

Note the class is `StatelessWidget` — with signals you rarely need `StatefulWidget` at all. Reach for
`StatefulWidget` when you need `InitState` / `Dispose` lifecycle or a ticker.

---

## API map

### Widgets and layout

| Flutter | Zigote | Notes |
|---|---|---|
| `StatelessWidget.build` | `StatelessWidget.Build` | Runs **once**; `Invalidate()` re-runs it |
| `StatefulWidget` + `State<T>` | `StatefulWidget` + `WidgetState<T>` | `CreateState()`, `InitState()`, `Dispose()` |
| `setState` | `SetState` | Mutates + relayouts; does **not** re-run `Build` |
| — | `SetStateRebuild` | What `setState` does in Flutter |
| `const` widgets | *(nothing)* | Nothing to optimize — trees are not rebuilt |
| `Container` | `Container` | `color:`, `padding:`, `margin:`, `decoration:`, `alignment:`, `constraints:` |
| `Column`/`Row` | `Column`/`Row` | Same alignment enums, plus a `spacing:` argument |
| `Expanded`/`Flexible`/`Spacer` | `Expanded`/`Flexible`/`Spacer` | Positional: `new Expanded(child, flex: 2)` |
| `Stack`/`Positioned` | `Stack`/`Positioned` | |
| `Padding`/`Center`/`Align` | `Padding`/`Center`/`Align` | `EdgeInsets.All/Symmetric/Only/FromLtrb` |
| `SizedBox` | `SizedBox` | `SizedBox.Shrink()` for the zero box |
| `LayoutBuilder` | `LayoutBuilder` | `Func<BuildContext, BoxConstraints, Widget>` |
| `Text` | `Label`, or `Text` | `Text` is an alias over `Label` taking a `TextStyle` |
| `RichText`/`TextSpan` | `RichText` | |
| `Icon(Icons.add)` | `new Icon(MaterialIcons.Add)` | Icon names are strings; `MaterialIcons` is generated |
| `Image.network` | `AsyncImage` | `Image` for local/decoded |
| `GestureDetector` | `GestureDetector` | `OnTap`, `OnDoubleTap`, `OnLongPressed`, hover callbacks |
| `InkWell` | `InkWell` (Material) / `Pressable` (kernel) | `Pressable` is the composable interaction primitive |
| `SingleChildScrollView` | `SingleChildScrollView` / `ScrollView` | |
| `ListView(children:)` | `ListView(children:)` | Virtualized layout; construction is O(n) — see [Lists](#lists) |
| `ListView.builder` | `ListView.Builder(count, i => …)` | Virtualizes construction too — rows built on demand |
| `GridView` | `GridView.Count(...)`, `ResponsiveGrid` | Sizes to content; wrap in a scroll view |
| `GridView.builder` | `GridView.Builder(cols, count, i => …)` | Virtualized and self-scrolling |
| `ReorderableListView` | `ReorderableList` | |
| `Draggable`/`DragTarget` | `Draggable<T>`/`DragTarget<T>` | Also handles OS file/text drops |
| `Scaffold`/`AppBar`/`FAB` | `Scaffold`/`AppBar`/`FloatingActionButton` | `Zigote.UI.Material` |
| `TextField`/`TextEditingController` | `TextField`/`TextEditingController` | `Zigote.UI.Material`; IME wired end to end |
| `DropdownButton` | `Dropdown<T>`/`DropdownButton` | |
| `Checkbox`/`Radio`/`Switch`/`Slider` | same names | `Zigote.UI.Material` |
| `TabBar`/`TabBarView` | `TabBar`/`TabBarView` | |
| `Card`/`Chip`/`Badge`/`Divider` | same names | |
| `SnackBar` | `app.ShowSnackbar(...)` | |
| `showDialog` | `new Dialog(content).Show()`, `Dialog.Alert/Confirm` | |
| `Tooltip` | `Tooltip`, or override `Widget.TooltipText` | |
| `SafeArea` | `SafeArea` | Backed by real device insets |
| `Theme.of(context)` | `Theme.Of(ctx)` | Returns `ThemeData` |
| `MediaQuery.of(context)` | `MediaQuery.Of(ctx)` | `.Size`, `.Padding`, `.ViewInsets`, `.SizeClass` |
| `InheritedWidget` | `InheritedWidget` | `ctx.DependOn<T>()` / `Read<T>()` / `Require<T>()` |
| `Key`/`ValueKey` | `Key`/`ValueKey<T>` | Only meaningful across `SetChildren` |
| `FutureBuilder` | `FutureBuilder<T>` | `AsyncSnapshot<T>` with `HasData`/`HasError`/`IsWaiting` |
| `StreamBuilder` | `Watch` over a `Signal<T>` | Or `Zigote.Reactive.R3` for operators |

### Navigation

| Flutter | Zigote |
|---|---|
| `Navigator.push(context, MaterialPageRoute(builder: ...))` | `await ctx.Push(new DetailPage(id))` |
| `Navigator.pushNamed(context, '/detail', arguments: x)` | `await ctx.PushNamed("/detail", arguments: x)` |
| `Navigator.pop(context, result)` | `ctx.Pop(result)` |
| `Navigator.maybePop` / `canPop` | `ctx.MaybePop()` / `ctx.CanPop()` |
| `pushReplacement` | `ctx.PushReplacement(route)` |
| `routes:` / `onGenerateRoute:` | `Routes` / `OnGenerateRoute` on `ZigoteApp` |
| Navigator 2.0 `pages:` / `onPopPage:` | `Pages` / `OnPopPage` |

`Push` returns a `Task<T?>` that completes with the pop result — the same shape as Flutter's
`Future`, so `await`-then-use ports directly.

### Animation

| Flutter | Zigote |
|---|---|
| `AnimationController(vsync: this)` | `new AnimationController(durationSeconds, vsync)` |
| `SingleTickerProviderStateMixin` | `SingleTickerProviderState<TWidget>` (a `WidgetState<T>` base) |
| `TickerProviderStateMixin` | `TickerProviderState<TWidget>` |
| `Tween`/`CurvedAnimation`/`Curves` | `Curves`, `AnimationController.Curve` |
| `FadeTransition` / `SlideTransition` / `ScaleTransition` | same names |
| `AnimatedOpacity` / `AnimatedAlign` / `AnimatedPadding` / `AnimatedContainer` | same names |
| `AnimatedSwitcher` | `AnimatedSwitcher` |
| `TweenAnimationBuilder` | `TweenAnimationBuilder<T>` |
| `flutter_animate`'s `.animate().fade()` | `widget.Animate().Fade(300.ms).Scale(delay: 100.ms)` |

### State management

| Flutter package | Zigote |
|---|---|
| `provider` / `InheritedNotifier` | `InheritedWidget`, or plain constructor injection |
| `riverpod` providers | `Signal<T>` + `Computed<T>` + `Watch` |
| `flutter_bloc` `Bloc`/`Cubit` | `Zigote.Bloc`'s `Bloc<TEvent, TState>` / `SyncBloc<,>` |
| `BlocBuilder` / `BlocSelector` | `new Watch(() => …)` reading `bloc.State.Value` |
| `BlocListener` | `OwnEffect(() => …)` or `bloc.Subscribe(...)` |
| `get_it` / `injectable` | Constructor injection at the composition root. There is no container. |

The `Bloc` port is close to mechanical:

```csharp
public abstract record CounterEvent
{
    public sealed record Bumped : CounterEvent;
    public sealed record Loaded(int Value) : CounterEvent;
}

public sealed record CounterState(int Value, bool Busy);

public sealed class CounterBloc(ICounters counters)
    : Bloc<CounterEvent, CounterState>(new CounterState(0, false))
{
    protected override async ValueTask OnEventAsync(CounterEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case CounterEvent.Bumped:
                Emit(Current with { Busy = true });
                Add(new CounterEvent.Loaded(await counters.BumpAsync(Restart())));
                break;
            case CounterEvent.Loaded(var value):
                Emit(new CounterState(value, false));
                break;
        }
    }
}
```

```csharp
new Watch(() => new Label($"{bloc.State.Value.Value}"))   // BlocBuilder
bloc.Add(new CounterEvent.Bumped());                      // context.read<Bloc>().add(...)
```

Differences worth knowing: the pump is **ordered and never nested** (an `Add` from inside a handler
runs after the current one finishes), **synchronous when the handler is** (no `await` means the state
has already changed when `Add` returns — tests assert without pumping), and `Restart()` gives you
latest-wins cancellation for free. A throwing handler is reported via `BlocErrors.OnError` and the
pump carries on.

---

## Lists

`ListView` virtualizes **measure, layout and paint** to the viewport — scrolling a 50,000-row list is
O(viewport). Two ways to fill it, and they differ in whether *construction* is virtualized too.

`ListView.Builder` is `ListView.builder`: rows are built when they enter the window and dropped when
they leave, so a million rows cost the same as ten.

```csharp
var list = ListView.Builder(items.Count, i => RowFor(items[i]), itemExtent: 36);
```

`GridView.Builder` is the same deal for grids — it builds one grid row of cells at a time and scrolls
itself (it returns the `ListView` of rows that does the virtualizing):

```csharp
var grid = GridView.Builder(crossAxisCount: 4, itemCount: photos.Count,
                            itemBuilder: i => new PhotoTile(photos[i]),
                            mainAxisSpacing: 8, crossAxisSpacing: 8);
```

The cost of laziness is the same one Flutter pays: **a row scrolled out is destroyed.** Anything the
row widget itself holds — hover, focus, a nested scroll offset, a running animation — dies with it.
Keep row state in your model and read it in the builder. When rows own state you cannot move out,
use `SetItems` with materialized widgets instead: that keeps every row alive, at ~8 µs each to build
and the memory of the whole set.

For a materialized list too big to build in one frame, fill across frames with `Background.Slice`:

```csharp
list.SetItems([], keepScroll);
if (count <= 400)
    for (var i = 0; i < count; i++) list.AddItem(build(i));
else
    // Slice owns the per-frame budget and the supersede-by-key rule: the list is the key, so a
    // query that changes on every keystroke cancels its own half-built predecessor.
    background.Slice(list, count, i => list.AddItem(build(i)));
```

For variable-height rows, set `HeightOf` (index → height) and the list keeps a prefix-sum table and
binary-searches the visible window — still O(viewport), and it works in builder mode, since the
height comes from the index and not from the built widget:

```csharp
_list.HeightOf = i => items[i].IsHeader ? 48f : 36f;
```

`HeightOf` is re-evaluated when the list's width changes, so a width-dependent height (wrapped text,
a grid cell's aspect ratio) stays correct across a resize.

---

## Gotchas, in the order you will meet them

**`Build` locals are frozen.** A `var label = 'Count: $_count'` inside `build` re-evaluates in Flutter
every rebuild. Here it evaluates once, at first measure. Move it into a `Watch` or onto a field.

**`SetState` will not change your tree's shape.** If the state decides *which* widgets exist, either
mutate the existing ones (`_switcher.Child = _pages[i]`) or use `SetStateRebuild`. `SetState` only
relayouts what is already there.

**Recreating widgets loses state.** In Flutter, `Button('x', onTap)` in `build` is free and the
element tree preserves state. Here it is a new object with no hover, no focus, no animation. Hoist to
a field and mutate.

**A `Watch` swap tears down its subtree.** Anything stateful inside it (a `ListView`'s scroll offset,
a focused `TextField`) is destroyed on every swap. Hoist the stateful child to a field, mutate it
inside the `Watch`, and return the same instance.

**The root background must be opaque.** wgpu clears with alpha 0. `ZigoteApp` handles this, but a raw
`App` root needs a `ColoredBox(theme.Background)` or the window is transparent.

**`Home` must be set before `Run()`.** `ZigoteApp.Run` captures `Home` into the root `Navigator`
*before* calling `OnInit`, so a tree built in `OnInit` is never mounted and the window comes up blank.
Set `Home` in the constructor; build its contents lazily on the first `Build` if they need services
`OnInit` creates.

**`Icons.add` is a string.** `MaterialIcons.Add` is a generated string constant, not an enum member.

**No `const` constructors.** They would do nothing.

**Async errors are yours.** There is no `FlutterError.onError`. Wire `BlocErrors.OnError` and
`Background.OnError`, or use `Zigote.Logging`'s `AppLog.CaptureFailures()` to route both into Serilog.

---

## What you gain

- **AOT and a single binary.** NativeAOT publish, no runtime install, ~10–20 MB, cold start in
  milliseconds. No Dart VM, no snapshot.
- **The whole .NET ecosystem** — `System.Text.Json`, EF Core, `HttpClient`, Roslyn, NuGet. This is
  usually the largest practical win over Dart.
- **A 3D engine in the same process.** Scenes, ECS, physics, shader graphs and widgets share one
  frame loop. There is no platform-view seam.
- **Genuinely native Linux.** `Zigote.UI.Adwaita` follows the system light/dark and accent live.
- **Zero-allocation paint** on the steady path, and headless testability with no device or
  `flutter_test` harness.

## What you lose

- **Accessibility.** The semantics tree is built; no platform bridge ships. Screen readers see
  nothing. This is the hard one.
- **Web.** No target.
- **Mobile maturity.** Bring-up, not a shipped platform. See `docs/mobile-port.md`.
- **pub.dev.** No third-party widgets. Anything not in the box, you write.
- **Text input edge cases.** IME composition is wired end to end and works, but Flutter has a decade
  of RTL, dead-key and platform-gesture fixes you are not inheriting.
- **DevTools depth.** There is a widget inspector and a repaint-rainbow overlay (Shift+D). There is no
  timeline, memory profiler or network pane — you use .NET's instead.
