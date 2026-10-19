# Signals & Watch

Application state lives in **signals**. A `Signal<T>` holds a value and remembers who read it. When the value changes, everything that read it is told, and nothing else.

| Primitive | Answers |
|---|---|
| `Signal<T>` | What is true now? |
| `Computed<T>` | What can be derived from it? (lazy, cached) |
| `Effect` | What side effect should react to it? |
| `Watch` | How does it reach the widget tree? |

```csharp
var count = new Signal<int>(0);
count.Value = 5;                 // write; equal values are ignored
count.Update(c => c + 1);        // read-modify-write
int now = count.Peek();          // read WITHOUT subscribing
```

## Watch

`Watch` runs its builder while tracking which signals it reads. When one changes, it builds a new subtree and swaps it in. Everything outside the `Watch` is untouched.

```csharp run height=260
class Demo : ComposedWidget
{
    private readonly Signal<string> _name = new("world");
    private readonly Signal<int> _size = new(24);

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwEntry { Text = _name.Peek(), Placeholder = "Your name", OnChanged = t => _name.Value = t },
            new Row(spacing: 8, children: [
                new AdwButton(label: "Smaller", onPressed: () => _size.Value = Math.Max(10, _size.Value - 4)),
                new AdwButton(label: "Bigger", onPressed: () => _size.Value += 4),
            ]),
            // Reads both signals, so it re-runs when either changes. The entry and the buttons never rebuild.
            new Expanded(new Center(new Watch(() => new Label($"Hello, {_name.Value}!", fontSize: _size.Value)))),
        ]));
}
```

Two rules make `Watch` work well:

- **Scope it tightly.** Wrap the thing that changes, not the page.
- **Hoist stateful children out of it.** A swap replaces the subtree, so a text field or list created inside the builder loses its caret or scroll position on every change. Keep such widgets in fields and return the same instance.

## Computed

`Computed.From` derives a value from other signals. It recomputes only when a signal it read changes, and only when someone actually reads it.

```csharp run height=300
class Demo : ComposedWidget
{
    private static readonly string[] All = ["Apple", "Apricot", "Banana", "Blueberry", "Cherry", "Grape", "Lemon", "Lime", "Mango", "Orange", "Peach", "Pear", "Plum"];
    private readonly Signal<string> _query = new("");
    private readonly Computed<string[]> _matches;

    public Demo() =>
        _matches = Computed.From(() => All.Where(f => f.Contains(_query.Value, StringComparison.OrdinalIgnoreCase)).ToArray());

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwSearchEntry { Placeholder = "Filter fruit", OnChanged = q => _query.Value = q },
            new Watch(() => new Label($"{_matches.Value.Length} of {All.Length} match", fontSize: 13)),
            new Expanded(new Watch(() => new Wrap(spacing: 6, runSpacing: 6,
                children: [.. _matches.Value.Select(f => new AdwButton(label: f, onPressed: () => { }) { Pill = true })]))),
        ]));
}
```

## Effect

An `Effect` runs immediately, then again whenever a signal it read changes. Inside a widget, register it with `OwnEffect` in `OnMount` so that it is disposed when the widget leaves the tree:

```csharp
protected override void OnMount() =>
    OwnEffect(() => Title = $"{_unread.Value} unread");
```

## Batching and threads

`Reactive.Batch(() => { a.Value = 1; b.Value = 2; })` coalesces several writes into one update pass: one layout, one redraw.

Signals may be written **from any thread**. The graph takes a lock, and `Watch` moves its rebuild onto the UI thread for you, so a background task can simply assign `result.Value = data`. An `Effect` that touches widgets must be created with `EffectAffinity.Deferred` so that it runs on the frame loop (see *Async & Background Work*).

> Value types stored in signals should implement `IEquatable<T>`, which records do automatically. Equality is what lets an unchanged write be skipped.
