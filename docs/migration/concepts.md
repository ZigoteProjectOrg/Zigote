# The model: what is actually different

Read this before the framework-specific guide. Flutter, Compose, SwiftUI and WPF differ from each
other in syntax and idiom, but all four agree on one thing that Zigote does not: **that the UI is
described afresh whenever state changes, and the framework's job is to diff that description against
what is on screen.**

Zigote is retained mode. Widgets are ordinary, long-lived C# objects. You construct the tree once and
then *mutate* it. There is no diff pass, no element tree, no recomposition, no virtual DOM.

Everything below follows from that.

---

## 1. `Build` runs once

```csharp
public sealed class Header : ComposedWidget
{
    protected override Widget Build(BuildContext ctx)
    {
        Console.WriteLine("built");   // prints once, not once per frame, not once per state change
        return new Label("Hello");
    }
}
```

`Build` is called on the first `Measure` and the returned subtree is cached. It runs again only when
something explicitly marks the widget dirty: `Invalidate()`, `MarkNeedsBuild()`, an
`InheritedWidget` you depended on changing, or a hot reload.

**Consequences:**

- A local variable computed inside `Build` is computed once. Anything that must track state either
  lives in a field, or lives inside a `Watch`.
- Widget construction is not free and not idempotent-by-convention — do not treat `new Widget(...)`
  as a cheap description. It allocates a real object that will live for the screen's lifetime.
- There is no `const` widget optimization to reach for, because there is nothing to optimize: the
  tree is not rebuilt.

## 2. Widget fields *are* the state

Hover, press, focus, scroll offset, caret position, in-flight animation — all of it lives on the
widget instance. That is what retained mode buys, and it is why replacing an instance is expensive
in a way it is not in Flutter, Compose or SwiftUI.

```csharp
// GOOD — same instance, keeps hover/focus/scroll/animation
_label.Text = $"Clicked {count}×";

// BAD — a new instance every change: hover resets, focus is lost, entrance animations replay
app.Root = new Button($"Clicked {count}×", OnClick);
```

Application state (what the app *knows*) belongs in a `Signal<T>` or a `Bloc`. Interaction state
(what the user is *doing*) belongs on the widget. Do not mirror the second into the first.

## 3. Reactivity is `Signal` + `Watch`, not rebuild-the-world

`Signal<T>` is a fine-grained observable value. `Computed<T>` derives from signals and caches.
`Effect` runs a side effect when its reads change. All three auto-track — there is no dependency
list to declare.

```csharp
private readonly Signal<string> _query = new("");
private readonly Signal<ImmutableArray<Track>> _all = new([]);

// Recomputes only when _query or _all changes; readers of _results see a cached value otherwise.
private readonly Computed<ImmutableArray<Track>> _results;

public SearchPage()
{
    _results = Computed.From(() =>
        _query.Value is "" ? _all.Value
                           : [.._all.Value.Where(t => t.Title.Contains(_query.Value, StringComparison.OrdinalIgnoreCase))]);
}
```

`Watch` is the bridge from a signal into the tree. It runs its builder under dependency tracking and
swaps in a new subtree only when something it read changed:

```csharp
new Watch(() => new Label($"{_results.Value.Length} results"))
```

Scope a `Watch` as tightly as the thing that actually changes. A `Watch` around a whole page rebuilds
the whole page's subtree on every keystroke; a `Watch` around the result count rebuilds one label.

**A `Watch` swap replaces its subtree.** Stateful children inside it are torn down and re-created —
scroll position, focus and animation go with them. Hoist anything that must survive into a field and
return the same instance:

```csharp
private readonly ListView _list = new();   // hoisted: survives every rebuild

new Watch(() =>
{
    _list.SetItems(_results.Value.Select(RowFor).ToList());   // mutate, don't recreate
    return _list;
});
```

Signals may be written from any thread. An off-thread write is marshalled: the loop is woken and the
subtree swap happens on the UI thread in the next `Measure`.

## 4. Changing state does not re-run `Build`

This is the single most common surprise, whichever toolkit you came from. There is no
stateless/stateful split and no `setState`: a widget is a retained object, so its fields *are* its
state, and you mutate them and say what that invalidated.

```csharp
sealed class Counter : ComposedWidget
{
    private readonly Label _label = new("0");
    private int _count;

    protected override Widget Build(BuildContext ctx) => new Column
    {
        Children =
        {
            _label,
            new Button("Increment", () =>
            {
                _label.Text = (++_count).ToString();
                MarkNeedsLayout();          // relayout + repaint; Build does not re-run
            }),
        }
    };
}
```

The mutation must target the retained children — which is why `_label` is a field.

| Call | Rebuild | Relayout | Repaint | Use for |
|---|---|---|---|---|
| `MarkNeedsPaint()` | — | — | ✓ | Visual-only change; size provably unchanged (hover tint, animation tick) |
| `MarkNeedsLayout()` | — | ✓ | ✓ | **The default.** Mutated a child's text, size, visibility |
| `MarkNeedsBuild()` / `Invalidate()` | ✓ | ✓ | ✓ | The child *structure* must genuinely change |

Reach for `MarkNeedsBuild` only when the tree's shape depends on the state. If you find yourself
calling it on every keystroke, the answer is an `OwnEffect`, a `Watch`, or a mutated child — not a
rebuild.

## 5. The frame

```
Measure(Constraints) → Layout(Offset) → DispatchEvents → Paint(PaintList)
```

- **Measure** is bottom-up and returns a `Size`, cached per widget.
- **Layout** is top-down and sets each widget's absolute `Bounds`.
- **Events** dispatch after layout, so `Bounds` is valid for hit-testing.
- **Paint** emits flat `ZgPaintCommand` structs into a reused buffer — zero allocation on the
  steady path.

Measure and Layout only run when something marked the tree dirty. A mouse-move repaints; it never
re-lays-out. Damage is tracked per widget (`DamageBounds`), so a hover glow repaints its own
rectangle, not the window.

If you write a custom widget, you implement exactly these three methods plus `HitTest`. Most widgets
should not: compose from the layout kernel + `DecoratedBox` + `Pressable` instead. Hand-written
`Measure`/`Layout`/`Paint` is for genuine primitives — canvases, virtualized lists, text editors,
animated thumbs.

## 6. Keys and reconciliation

Reconciliation exists, but only where a list of children is *replaced wholesale*:
`MultiChildWidget.SetChildren`. Give children a `Key` and the reconciler reuses the retained
instances across insert / remove / reorder instead of rebuilding them.

```csharp
row.SetChildren(items.Select(i => new RowWidget(i) { Key = new ValueKey<int>(i.Id) }));
// instances for surviving ids are reused; their scroll/hover/animation state survives the reorder
```

Without keys, `SetChildren` matches positionally — fine for a static list, wrong for anything the
user reorders.

## 7. Inherited data

`InheritedWidget` propagates data down the tree; `ThemeProvider` and `MediaQuery` are the built-in
ones, injected by `ZigoteApp` at the root.

```csharp
var theme = Theme.Of(ctx);              // registers ctx's builder as a dependent
var media = MediaQuery.Of(ctx);
var isPhone = media.SizeClass == WindowSizeClass.Compact;
```

`DependOn<T>()` registers a dependency and rebuilds the reading widget when the data changes.
`Read<T>()` / `FindAncestor<T>()` look up without subscribing. Write your own by subclassing
`InheritedWidget` and overriding `UpdateShouldNotify`.

For app-wide services, prefer plain constructor injection at the composition root over an inherited
widget. Zigote has no DI container and does not want one — pass an `AppEnv` record down.

## 8. Threading

One UI thread. The frame loop, layout, paint and event dispatch all run on it.

- Signal writes from any thread are legal and are marshalled.
- `App.Post(action)` queues work onto the UI thread, drained at the top of the next frame.
- `Zigote.Core.Threading.Background` is the worker pool: `Run`, `RunAsync`, `Latest()` for
  latest-wins work, `Slice` for chunked work with a per-frame budget.
- A `Bloc` owns its own concurrency: `Restart()` cancels the previous unit of work, `Track()` ties a
  subscription to the bloc's lifetime, `Dispose()` cancels everything.

Never block the frame. See [`cookbook.md`](cookbook.md#background-work-without-hitching-the-frame).

## 9. Mount lifetime and disposal

`OnMount` runs when the widget enters the tree and `OnUnmount` when it leaves — paired 1:1, so a
re-attached widget mounts again. Anything you subscribe to must be scoped to that period, because
**signals hold their observers strongly** — a bare `new Effect(...)` outlives the widget and keeps
running against a detached tree.

```csharp
protected override void OnMount()
{
    OwnEffect(() => _label.Text = _bloc.State.Value.Title);   // tracked, disposed on unmount
    Own(_service.Changed.Subscribe(OnChanged));          // same for any IDisposable
}
```

Everything registered with `Own`/`OwnEffect`/`CreateTicker` is released automatically —
override `OnUnmount` only for teardown those cannot express.

One-time composition — building the child widgets you keep in fields — belongs in the **constructor**,
not `OnMount`: it should happen once per instance, not once per mount.

## 10. Hot reload

Edit a widget's `Build()` while the app runs and the live UI updates without a restart. Widget
instances (and every field they hold) are preserved; only `Build()` re-runs. Run under `dotnet watch`,
or use Rider / VS "apply changes".

Constructor bodies, field initializers, `OnMount`, and native Zig / shader changes still need a
full restart — which is the usual reason a hot-reloaded change "did nothing".

---

## The five rules, condensed

1. Never recreate a widget you can mutate.
2. State that outlives a frame goes in a field, a `Signal`, or a `Bloc` — never a `Build` local.
3. `Watch` is how a signal reaches the tree. Scope it tight; hoist stateful children out of it.
4. Mutate and `MarkNeedsLayout`. `MarkNeedsBuild` is the exception, not the habit.
5. Scope what you subscribe to with `Own`/`OwnEffect`/`Bind` in `OnMount`.
