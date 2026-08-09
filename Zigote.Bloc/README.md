# Zigote.Bloc

**Events in, ordered, one at a time; state out as signals.** The app-side half of the pattern every Zigote app had its
own copy of — Mahou, Skies and Timbre each wrote the same pump and each got a different detail wrong.

```csharp
public abstract record CounterEvent
{
    public sealed record Bumped : CounterEvent;
    public sealed record Loaded(int Value) : CounterEvent;
}

public sealed record CounterState(int Value, bool Busy);

public sealed class CounterBloc(ICounters counters) : Bloc<CounterEvent, CounterState>(new(0, false))
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

new Watch(() => new Text($"{bloc.State.Value.Value}"))   // rebuilds exactly this subtree
bloc.Add(new CounterEvent.Bumped());                     // the only way state ever changes
```

## The three bases

| Type                       | For                                                                                         |
|----------------------------|---------------------------------------------------------------------------------------------|
| `Bloc<TEvent, TState>`     | One immutable state record. The default.                                                    |
| `SyncBloc<TEvent, TState>` | Same, when no handler awaits — saves a `return ValueTask.CompletedTask` per exit.           |
| `Bloc<TEvent>`             | Several independent signals, where one record would make every write a whole-state rewrite. |

## What the pump guarantees

- **Ordered, never nested.** An `Add` from inside a handler runs *after* the current one finishes.
- **Synchronous when the handler is.** `Add` on a quiet bloc has already run its handler by the time it returns — a tap
  feels immediate and a test asserts without polling. A handler that awaits releases the caller at its first real await;
  events that arrive meanwhile wait their turn.
- **Allocation-free dispatch** on that synchronous path: no state machine, no `Task`. Only a handler that actually
  awaits pays for one.
- **One bad event does not take the screen down.** A throwing handler is reported through
  `BlocErrors.OnError` and the pump carries on. Unset, failures go to `DebugLog`; `Zigote.Logging`'s
  `AppLog.CaptureFailures()` routes them to Serilog instead.
- **One unit of work in flight.** `Restart()` cancels the previous one and hands back a token for its replacement, so
  type → switch source → refresh ends with the *refresh*'s result, not whichever request happened to land last.
- **Dispose is the off switch.** Tracked subscriptions (`Track`) die with the bloc, in-flight work is cancelled, and a
  dead bloc drops events rather than throwing at whoever still holds it. A handler that resumes after the bloc has gone
  reads `Lifetime` as cancelled — the token outlives its source.

## Watching every bloc at once

`BlocObserver` is the seam for a log, a DevTools timeline or a replay. Both hooks are process-wide and unset by default,
so a bloc that nobody is watching pays one null check per event:

```csharp
BlocObserver.OnEvent  = (bloc, e)        => timeline.Add($"{bloc.GetType().Name} ← {e}");
BlocObserver.OnChange = (bloc, from, to) => timeline.Add($"{bloc.GetType().Name} {from} → {to}");
```

They fire on the pump in the order things happened, so the two interleave into one readable log without correlation ids.
`OnChange` skips emits the signal deduplicated — a transition the widget tree never saw is not on the timeline. A
throwing hook goes to `BlocErrors.OnError` and is otherwise ignored; observation cannot break the feature it observes.

## Dropping an event instead of queueing it

The queue is strictly sequential, so a double-tapped submit runs twice. There is no `droppable` transformer to reach
for — `Add` is `virtual` and `Current` is right there:

```csharp
public override void Add(CounterEvent e)
{
    if (Current.Busy && e is CounterEvent.Bumped) return; // second tap, same in-flight work
    base.Add(e);
}
```

Guard on the state rather than on a private "am I handling something" flag: an `Add` from *inside* a handler is a
supported ordering, and a flag that is true for the whole dispatch would swallow it.

No package dependencies — `Zigote.Core` only. A bloc that needed a logging package would push that package onto every
app using the pattern.
