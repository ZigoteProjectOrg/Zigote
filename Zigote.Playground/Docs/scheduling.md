# Scheduling

Zigote runs everything that touches widgets on **one UI thread**, in one frame loop. Scheduling is about deciding *when* each piece of work runs, so that the loop always has time to present the next frame. At 120 Hz that is 8.3 ms, layout and paint included.

## One turn of the loop

1. **Wait.** If nothing is dirty and no ticker is running, the loop sleeps in `WaitEvents` until input, a posted action or a background result wakes it. An idle app costs nothing.
2. **Poll** OS input into a buffer.
3. **Deferred effects.** `Effect`s created with `EffectAffinity.Deferred` run here (`Reactive.DrainDeferred`).
4. **Posted actions.** Everything queued with `App.Post` runs, oldest first. Set `App.PostDrainBudgetMs` to cap this step; leftovers then run on later frames, in order.
5. **Background budget.** `Deliver.WhenIdle` results and `Slice` steps run against what is left of the frame's budget.
6. **Layout**, only if something is dirty.
7. **Dispatch input** against the fresh layout.
8. **Tick** animations and tickers, after input, so a drag handled this frame moves this frame.
9. **Paint and submit**, only the damaged regions.

So there are several distinct places your code can run, and choosing among them is what scheduling means here:

| You want to… | Use | Runs |
|---|---|---|
| React to input | event handlers, `onPressed` | Step 7, immediately |
| Update UI from another thread | write a `Signal`; or `App.Post(action)` | Next frame, step 4 (or the next measure for a `Watch`) |
| Run a side effect when state changes | `OwnEffect` (inline) / `new Effect(…, EffectAffinity.Deferred)` | On the writing thread / step 3 |
| Do slow work | `Background.Run`, `RunAsync` | Thread pool; result back at step 4 |
| Deliver many small results | `Background.Post(…, Deliver.WhenIdle)` | Step 5, only while the frame has room |
| Process a big collection on the UI thread | `Background.Slice` | Step 5, a few items per frame |
| Debounce, latest wins | `Background.Latest()` | Thread pool, previous run cancelled |
| Animate | `Ticker`, controllers, springs | Step 8, every frame while active |

## Slicing big work across frames

Some work *must* run on the UI thread, such as creating 100,000 rows. Doing it in one go blocks the frame for its whole duration, and every frame that should have been drawn meanwhile is dropped. `Slice` spreads it across frames within the per-frame budget: the window stays responsive and the list fills in over a few frames. Compare the two buttons, and try scrolling or hovering while each runs:

```csharp run height=360
class Demo : ComposedWidget
{
    private const int Count = 100_000;
    private readonly ListView _list = new(itemExtent: 28);
    private readonly Signal<string> _status = new("Pick a strategy");

    private static Widget Row(int i) => new Padding(EdgeInsets.Symmetric(horizontal: 12), new Label($"Row {i:N0}", fontSize: 13));

    private void AllAtOnce()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _list.SetItems([], keepScroll: false);
        for (int i = 0; i < Count; i++) _list.AddItem(Row(i));
        _status.Value = $"All at once: the UI thread was blocked for {clock.ElapsedMilliseconds} ms";
    }

    private void Sliced()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _list.SetItems([], keepScroll: false);
        _status.Value = "Slicing…";
        // The list is the key: starting again supersedes a fill that is still running.
        Owner!.Background.Slice(_list, Count, i => _list.AddItem(Row(i)),
            onDone: () => _status.Value = $"Sliced: filled over {clock.ElapsedMilliseconds} ms without blocking a frame");
    }

    protected override Widget Build(BuildContext context) =>
        new Column(crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Padding(EdgeInsets.All(12), new Row(spacing: 8, children: [
                new AdwButton(label: "All at once", onPressed: AllAtOnce) { Style = AdwButtonStyle.Destructive },
                new AdwButton(label: "Sliced", onPressed: Sliced) { Style = AdwButtonStyle.Suggested },
                new AdwSpinner(),
            ])),
            new Padding(EdgeInsets.Symmetric(horizontal: 12), new Watch(() => new Label(_status.Value, fontSize: 13))),
            new Expanded(_list),
        ]);
}
```

The spinner is a frame-rate meter: it stutters during "All at once" and keeps spinning during "Sliced".

## Latest wins

A search box fires on every keystroke. You want to wait until typing pauses (**debounce**), and never let a slow early search overwrite a fast later one (**latest wins**). `Latest` does both: each `Run` cancels the previous one, including one still waiting out its delay.

```csharp run height=300
class Demo : ComposedWidget
{
    private static readonly string[] Words = ["signal", "widget", "layout", "paint", "ticker", "render", "theme", "locale", "shader", "spring", "slice", "scroll", "sprite", "socket"];
    private readonly Signal<string> _result = new("Type to search");
    private readonly Signal<int> _started = new(0), _delivered = new(0);
    private Latest? _search;

    protected override void OnMount() => _search = Own(Owner!.Background.Latest());

    private void OnQuery(string q)
    {
        _started.Value++;
        _search!.Run(
            work: ct =>
            {
                Thread.Sleep(Random.Shared.Next(100, 600));   // a "server" with variable latency
                ct.ThrowIfCancellationRequested();
                return Words.Where(w => w.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
            },
            onUi: hits =>
            {
                _delivered.Value++;
                _result.Value = hits.Length == 0 ? "No matches" : string.Join(", ", hits);
            },
            delay: TimeSpan.FromMilliseconds(200));
    }

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwSearchEntry { Placeholder = "Search words", OnChanged = OnQuery },
            new Watch(() => new Label($"keystrokes: {_started.Value}   ·   searches delivered: {_delivered.Value}", fontSize: 12)),
            new Watch(() => new Label(_result.Value, fontSize: 16)),
        ]));
}
```

Type quickly: many keystrokes, few deliveries, and the result always matches the current text.

## Background scopes

`App.Background` is the app's root scope; the frame loop drains it automatically. Give each screen or feature a child scope and dispose it when the feature goes away. Everything it started is cancelled, and late results are dropped instead of landing on a dead screen:

```csharp
private Background? _work;

protected override void OnMount() => _work = Own(Owner!.Background.Child("library"));

void Rescan() => _work!.Run(() => Scanner.Scan(folder), tracks => _tracks.Value = tracks);
```

Failures in background work are never silent. They go to `Background.OnError`, which logs through `DebugLog` by default and is tagged with the scope path and calling method.

## Tuning knobs

| Setting | Default | Effect |
|---|---|---|
| `App.PostDrainBudgetMs` | 0 (drain all) | Cap time spent on posted actions per frame; the rest wait for the next frame, in order |
| `App.TryPost` + `PostCapacity` | 0 (no ceiling) | Reject instead of queueing when the UI is flooded |
| `App.BackgroundMaxDeferralMs` | 0 | Let late frames skip background work entirely, within this bound |
| `App.FrameReserveMs` | measured | The time kept free for layout, paint and submit (read-only, diagnostics) |
| `App.RequestExtraFrames(n)` | — | Keep drawing for `n` more frames, for effects that settle over time |

## Rules of thumb

- **Never block the UI thread.** No `.Result`, no `Thread.Sleep`, no synchronous file or network I/O in handlers or `Build`.
- **Background threads write signals and nothing else.** Mutating a widget from a worker is a race; post it.
- **Effects that touch widgets are `Deferred`.** Inline effects run on whichever thread wrote the signal.
- **Batch related writes** with `Reactive.Batch` so they cost one layout, not several.
- **Don't poll from the frame loop.** A ticker that checks "is it done yet?" every frame keeps the app awake forever. Have the work notify you instead.
- **Prefer `Latest` over hand-rolled cancellation tokens**, and child scopes over bare `Task.Run`.
