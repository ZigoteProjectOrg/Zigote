# Reactivity in Depth

*Signals & Watch* covered the everyday shape. This page covers the whole reactive core: what each primitive guarantees, how dependencies are tracked, and the tools for the cases a plain signal handles badly.

Everything lives in `Zigote.Core.State` and has no dependency on widgets, so the same signals drive a view model, a background service or a unit test.

| Primitive | Question it answers |
|---|---|
| `Signal<T>` | What is true now? |
| `Computed<T>` | What can be derived? Lazy, cached, glitch-free |
| `Effect` | What imperative work should react? |
| `LinkedSignal<T>` | Derived, but the user may override it locally |
| `Trigger` | *That* something happened, with no value |
| `Watch` | How does it reach the widget tree? |
| `Reactive` | Batching, untracked reads, diagnostics |

## How tracking works

There are no dependency lists. While a `Computed`, `Effect` or `Watch` body runs, every `.Value` read is recorded, and those signals become its sources. The sources are re-recorded on every run, so a branch that stops reading a signal stops depending on it:

```csharp
var showDetails = new Signal<bool>(false);
var details = new Signal<string>("…");
var text = Computed.From(() => showDetails.Value ? details.Value : "hidden");
// While showDetails is false, writing `details` does not recompute `text` at all.
```

`Peek()` reads without recording. Use it in event handlers and anywhere you need the current value without subscribing. `Reactive.Untracked(() => …)` does the same for a whole block.

## Writing

```csharp
count.Value = 5;                  // skipped if equal to the current value
count.Set(5);                     // always notifies, even if equal
count.Update(c => c + 1);         // read-modify-write under the graph lock
using var sub = count.Subscribe(v => Log(v));   // fires now, then on every change
count.Changed += v => Log(v);     // fires on changes only
```

Equality decides what counts as a change. Pass an `IEqualityComparer<T>` to the constructor to change it, for example to compare lists by content. Value types should implement `IEquatable<T>`, which records do automatically.

## Computed: lazy, cached, glitch-free

A `Computed` runs only when someone reads it, and only if a source changed since the last run. When it is observed by an effect or a `Watch`, it is **glitch-free**: in a "diamond", where two computeds share a source and a third reads both, a change runs the bottom one exactly once and never shows it a half-updated mix.

The example counts runs. Change the first name: `full` and `initials` each recompute once, and `badge`, which reads both, runs once, not twice. Then press **Rename (unbatched)** and **Rename (batched)** and compare the counters.

```csharp run height=340
class Demo : ComposedWidget
{
    private readonly Signal<string> _first = new("Ada"), _last = new("Lovelace");
    private readonly Signal<int> _fullRuns = new(0), _initialsRuns = new(0), _badgeRuns = new(0);
    private readonly Computed<string> _full, _initials, _badge;

    public Demo()
    {
        _full = Computed.From(() => { Bump(_fullRuns); return $"{_first.Value} {_last.Value}"; });
        _initials = Computed.From(() => { Bump(_initialsRuns); return $"{_first.Value[..1]}{_last.Value[..1]}"; });
        // The diamond's bottom: reads two computeds that share both sources.
        _badge = Computed.From(() => { Bump(_badgeRuns); return $"[{_initials.Value}] {_full.Value}"; });
    }

    // Counters are bumped untracked, so counting never becomes a dependency.
    private static void Bump(Signal<int> s) => Reactive.Untracked(() => s.Value = s.Peek() + 1);

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Row(spacing: 8, children: [
                new Expanded(new AdwEntry { Text = "Ada", Placeholder = "First", OnChanged = t => _first.Value = t.Length == 0 ? "?" : t }),
                new Expanded(new AdwEntry { Text = "Lovelace", Placeholder = "Last", OnChanged = t => _last.Value = t.Length == 0 ? "?" : t }),
            ]),
            new Watch(() => new Label(_badge.Value, fontSize: 22)),
            new Row(spacing: 8, children: [
                new AdwButton(label: "Rename (unbatched)", onPressed: () => { _first.Value = "Grace"; _last.Value = "Hopper"; }),
                new AdwButton(label: "Rename (batched)", onPressed: () => Reactive.Batch(() => { _first.Value = "Alan"; _last.Value = "Turing"; })),
            ]),
            new Watch(() => new Label(
                $"full ran {_fullRuns.Value}×   ·   initials ran {_initialsRuns.Value}×   ·   badge ran {_badgeRuns.Value}×",
                fontSize: 13)),
        ]));
}
```

Unbatched, the two writes are two separate updates, so `badge` runs twice. Batched, they become one update and `badge` runs once.

Other guarantees:

- **Leak-free.** An unobserved computed neither recomputes nor is kept alive by its sources.
- **Exceptions are cached.** A throwing body rethrows on every read until a source changes. It doesn't retry every frame.
- **Cycles throw** instead of hanging.
- An intermediate computed whose value didn't change doesn't wake the readers below it.

## Effect

An `Effect` runs once immediately, then again whenever a source it read changes. Return a cleanup function, and it runs before the next run and on dispose, like React's `useEffect`:

```csharp run height=220
class Demo : ComposedWidget
{
    private readonly Signal<bool> _running = new(false);
    private readonly Signal<int> _seconds = new(0);

    protected override void OnMount() =>
        // Owned: disposed with the widget. Re-runs whenever _running changes.
        OwnEffect(() =>
        {
            if (!_running.Value) return () => { };
            // A thread-pool timer writing a signal: legal from any thread.
            var timer = new Timer(_ => _seconds.Value++, null, 1000, 1000);
            return () => timer.Dispose();   // cleanup: runs when _running flips, and on unmount
        });

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 14, children: [
            new Watch(() => new Label($"{_seconds.Value} s", fontSize: 40)),
            new AdwSwitch(value: false, onChanged: v => _running.Value = v),
        ]));
}
```

**Where an effect runs:**

| Affinity | Runs |
|---|---|
| `EffectAffinity.Inline` (default) | Immediately, on whichever thread wrote the signal, while the graph lock is held. Only for bodies that touch reactive state |
| `EffectAffinity.Deferred` | On the UI thread, at the start of the next frame. **Required** for anything that touches widgets, blocks, or takes another lock |

## LinkedSignal

Some local state should follow a source *and* accept overrides: the selected item in a list that resets when the list changes, or a draft field that reloads when the edited record changes. `Linked.From` is a writable signal seeded by a computation. Your writes stick until a source changes, and then it snaps back:

```csharp run height=260
class Demo : ComposedWidget
{
    private static readonly Dictionary<string, string[]> Menu = new() {
        ["Fruit"] = ["Apple", "Banana", "Cherry"],
        ["Veg"] = ["Carrot", "Leek", "Pepper"],
        ["Bread"] = ["Baguette", "Rye", "Sourdough"],
    };
    private readonly Signal<string> _category = new("Fruit");
    private readonly LinkedSignal<string> _pick;

    // Follows the category (resets to its first item), but the user may pick another.
    public Demo() => _pick = Linked.From(() => Menu[_category.Value][0]);

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 14, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new AdwToggleGroup(labels: [.. Menu.Keys], onActive: i => _category.Value = Menu.Keys.ElementAt(i)),
            new Watch(() => new Wrap(spacing: 8, runSpacing: 8, children: [
                .. Menu[_category.Value].Select(item => new AdwChip(item, selected: item == _pick.Value, onPressed: () => _pick.Value = item)),
            ])),
            new Watch(() => new Label($"Selected: {_pick.Value}", fontSize: 18)),
        ]));
}
```

Pick "Cherry", then switch category: the selection resets to the new category's first item.

## Trigger

A `Trigger` is a source with no value, for "it happened" events such as a manual refresh or an external cache invalidation. Readers call `Depend()`; `Fire()` invalidates them:

```csharp run height=180
class Demo : ComposedWidget
{
    private readonly Trigger _refresh = new();
    private readonly Computed<string> _stamp;

    public Demo() => _stamp = Computed.From(() =>
    {
        _refresh.Depend();                       // re-run whenever the trigger fires
        return DateTime.Now.ToString("HH:mm:ss.fff");
    });

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new Watch(() => new Label(_stamp.Value, fontSize: 30)),
            new AdwButton(label: "Refresh", onPressed: _refresh.Fire) { Pill = true },
        ]));
}
```

## Collections

Signals notify on *assignment*, not mutation. Store immutable values and replace them:

```csharp
// ✗ Mutating the list in place: the signal never knows.
_items.Value.Add(item);

// ✓ Replace with a new list (or ImmutableArray / ImmutableList).
_items.Value = [.. _items.Value, item];
_items.Update(list => list.Remove(old));        // with ImmutableList<T>
```

For large lists, keep the collection in one signal and derive views with `Computed`: filtered, sorted, counted. Don't keep a signal per row unless rows really change independently.

## Threading

The graph is protected by one re-entrant lock, so any thread may write any signal. What runs where is explicit: a `Watch` always rebuilds on the UI thread, inline effects run on the writer's thread, and deferred effects run on the frame loop. `Reactive.LockTimeoutMs` turns a would-be deadlock into a `ReactiveDeadlockException` that names both threads.

## Debugging

DevTools' Reactive panel (and the variables `reactive.writes`, `reactive.runs`, `ui.watch_rebuilds`) shows how much the graph is doing. The number to check is the **idle** rate: anything ticking while nothing on screen changes is a bug. Typical causes are a computed that builds a new collection every run, an effect that writes a signal it reads, or a value type without equality. `Reactive.TrackReactions` names *which* body is churning.

## Signals or a Bloc?

Signals are the state primitive. A `Bloc` (from `Zigote.Bloc`) sits on top for screens with real behaviour: an ordered event queue, one immutable state record, `Restart()` for latest-wins cancellation, and a timeline you can observe. Start with signals; reach for a bloc when the event handling outgrows a few lambdas.
