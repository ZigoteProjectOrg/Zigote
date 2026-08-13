# Zigote.Bloc.Benchmark

**What a dispatch costs, what observation costs, and what the pump's one lock costs as producers pile on.**

```
dotnet run -c Release --project Zigote.Bloc.Benchmark                          # everything
dotnet run -c Release --project Zigote.Bloc.Benchmark -- --filter *Dispatch*   # vs a channel pump
dotnet run -c Release --project Zigote.Bloc.Benchmark -- --filter *Contention* # the thread sweep
```

## The three suites

| Type                       | Question                                                                     |
|----------------------------|------------------------------------------------------------------------------|
| `BlocBenchmarks`           | What does one event cost, and does it allocate what the README says it does? |
| `DispatchComparison`       | Inline dispatch vs a queue a scheduler drains — the `bloc_signals` argument. |
| `BlocContentionBenchmarks` | What does the pump's lock cost at 1, 2, 4, 8, 16 producers?                  |

Every row exists because something in `Zigote.Bloc/README.md` promises it. A promise nobody measured is a promise that
quietly stops being true — `AddWithoutEmitting` must allocate zero, `AddSync` must be one state record, and
`ValueChangeSkipsSelectWatcher` must never wake its watcher.

## Reading the contention rows

Total work is held constant (32k events however many threads share them) and `OperationsPerInvoke` is that same
constant, so **Mean reads directly as nanoseconds per event** and rows are comparable straight down. Flat means adding
producers is free; rising means they are serialising.

`SharedBloc` is timed until the last event has actually been *handled*, not until the last `Add` returned. Under
contention only one caller wins `_pumping` and drains for everyone else, so a benchmark that stopped at the last
`Add` would be timing the queue rather than the work. That single-consumer ceiling is the design — it is what keeps
handlers from overlapping — and this is where it becomes visible instead of surprising.

## Reading the comparison rows

The baseline is `System.Threading.Channels` with one reader loop: the in-box shape of "producer writes, a consumer
drains later", which is what a stream-based pump is once the Rx vocabulary comes off. Both sides do identical work —
take an event, build a state record, write it to a `Signal<T>` through `Reactive.Sync`.

Rows read as **event accepted → state actually readable**. The channel rows include a wait because that is where the
work finishes; the Zigote rows include no synchronisation because there is nothing to wait for. That asymmetry is the
measurement, not a thumb on the scale.

`ChannelSingleRoundTrip` is a thread handoff, so it is noisy by nature — its magnitude is the point, not its third
significant figure.

## What is deliberately not here

- **No head-to-head against another C# bloc library.** `Zigote.Reactive.Benchmark` has one against SignalsDotnet because
  a rival reactive core exists to port the shapes from. There is no equivalent .NET bloc to port; the channel pump in
  `Baseline.cs` is the honest stand-in for the dispatch model that is actually being argued against.
- **No numbers checked into this file.** They are machine- and load-dependent, and a stale table in a README is worse
  than no table — run the suite.
- **No correctness-under-contention probe.** That lives in `Zigote.Tests/BlocConcurrencyTests.cs` as assertions, because
  unlike a third-party library's behaviour, these are guarantees this repo makes and has to keep.
