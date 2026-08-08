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
  dead bloc drops events rather than throwing at whoever still holds it.

No package dependencies — `Zigote.Core` only. A bloc that needed a logging package would push that package onto every
app using the pattern.
