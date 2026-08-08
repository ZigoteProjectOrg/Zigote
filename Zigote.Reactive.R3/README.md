# Zigote.Reactive.R3

**The one bridge between an app's two reactive primitives**, so neither layer has to know the other's.

The UI is built on Zigote `Signal<T>` — `Watch` subscribes to it, and a widget rebuild is a signal read. Data and domain
layers are built on R3 `Observable<T>`, which is where the operators live:
debounce, latest-wins, merge. Cross the boundary once at each end — a bloc turns repository streams into events on the
way in and publishes a signal on the way out — so nothing in between holds both.

```csharp
// Signal → stream: the current value first, then every change.
prefs.Units.AsStream()                      // Preference<T> too — same subscribe-and-replay contract
     .Select(u => u == Imperial)
     .Subscribe(...);

query.AsChangeStream()                      // edges only, skipping the value it already holds
     .Debounce(TimeSpan.FromMilliseconds(250))
     .SubscribeAwait(...);

// Stream → signal, seeded until the first value arrives. The handle owns the subscription.
using var rows = repository.Rows.ToSignal(initial: []);
new Watch(() => new ListView(rows.Value));
```

Writes into the graph go through `Reactive.Sync`: a stream may emit from any thread, and every signal write belongs
under the graph lock.

Nothing here is a replay buffer — the signal stays the single source of truth. Values arrive on whichever thread wrote
it; add an `ObserveOn` if the consumer cares which one.
