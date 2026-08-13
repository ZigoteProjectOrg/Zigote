# Zigote.UI.FSharp

F# ergonomics for Zigote — and deliberately nothing more. The UI itself is the C# widget API, used
directly: F# constructor calls take named arguments and set properties inline, so there is no view
DSL, no attribute vocabulary, no code generator, and no second reconciler to keep in sync with the
retained tree. What this package adds is the part C# interop makes noisy from F#: terse reactive
constructors, `Signal` combinators, and a host bootstrap.

```fsharp
open Zigote.UI.FSharp

let count = signal 0
let doubled = computed (fun () -> count.Value * 2)

let view =
    Column(
        mainAxisSize = MainAxisSize.Min,
        spacing = 8f,
        children =
            [| w (watch (fun () -> Text($"count {count.Value} · doubled {doubled.Value}")))
               Button("+", (fun () -> count.Value <- count.Value + 1))
               Button("Reset", (fun () -> count.Value <- 0), Style = ButtonStyle.Flat) |])

Host.run (AppConfig.create "Counter" ThemeData.Dark) view
```

There was once a VDOM, an attr DSL and an Elmish MVU loop here. They were deleted on purpose —
each duplicated something the retained engine already does better, and the full argument is written
down in [`docs/notes/fsharp-module-simplification.md`](../docs/notes/fsharp-module-simplification.md).

## The surface

Two files, ~180 lines, all of it in `namespace Zigote.UI.FSharp`.

| | What it is |
|---|---|
| `signal v` / `computed f` / `effect f` | Terse constructors for the C# `Signal<'T>`, `Computed<'T>`, `Effect` — auto-tracking, one graph for the whole engine. |
| `computedEq eq f` | A computed whose change propagation is gated by a custom equality — a recompute to an "equal" value does not wake observers. |
| `effectWith f` | Effect whose body returns a cleanup thunk, run before each re-run and on dispose. Allocation-free on re-run. |
| `batch f` / `untracked f` | `Reactive.Batch` / `Reactive.Untracked`, curried. |
| `Signal.map` / `map2` / `map3` / `bind` | Derived values over any readable (`Signal` or `Computed`). |
| `Signal.subscribe` / `readonly` / `create` | Change callbacks; read-only upcast; explicit construction. |
| `watch f` | The C# `Watch` widget over an F# builder: `f` re-runs, and its subtree is swapped, whenever a signal it read changes. |
| `w x` | Upcast any widget to `Widget` — for the head of a mixed child array, which F# types from its first element. |
| `AppConfig` / `Host.run` | Boot a standalone window: title, theme, size, and an `OnReady: App -> unit` hook for host setup (installing DevTools, for one). |

`w` and `watch` are the only widget helpers because those are the only two places F# will not infer
the upcast for you. Everything else — layout, controls, theming, navigation — is the C# API as-is;
see [`Zigote.UI/README.md`](../Zigote.UI/README.md).

## The rules `watch` imposes

`watch` **rebuilds** its subtree; it does not patch it. Fine-grained updates fall out of that — and
so do a few rules. All of them were found the hard way, running the gallery:

- **A signal read while a widget is being constructed becomes a dependency of the enclosing
  `watch`.** Seeding an input with `sig.Value` inside a tab-level `watch` rebuilds the whole tab on
  every keystroke or drag frame. Seed with `sig.Peek()` (untracked) and let the widget own its
  interaction state, writing back through its change callback:

  ```fsharp
  Slider(step.Peek(), min = 1f, max = 10f, onChanged = fun v -> step.Value <- v)
  ```

- **Never wrap a focusable or editable widget in a `watch` keyed on what the user is typing into
  it.** The rebuild replaces the instance, and focus and caret go with it. Keep the instance in a
  binding and push values into it imperatively:

  ```fsharp
  let newTodoField = TextField(onChanged = (fun v -> newTodo.Value <- v), Hint = "What needs doing?")
  let submitTodo () =
      addTodo ()
      newTodoField.Text <- ""   // clear the retained widget, don't rebuild it
  ```

- **Gate on a `computed`, not the raw signal.** A button that should enable and disable must not
  rebuild per keystroke:

  ```fsharp
  let canAdd = computed (fun () -> newTodo.Value.Trim() <> "")
  watch (fun () -> Button("Add", submitTodo, Enabled = canAdd.Value))   // rebuilds only on the flip
  ```

- **An animating control (Checkbox, Switch) is uncontrolled too**, unless something other than the
  control itself writes its signal — a `watch` around one replaces the widget mid-transition, so it
  snaps instead of animating.

Widgets that are pure output — labels, progress bars, anything stateless and cheap — live happily
inside a `watch`. That is the ordinary case, and it is what makes updates fine-grained.

For list rows that must survive a re-sort (an in-flight `TextField` edit, say), cache retained
instances by id and return the same ones from the rebuild — or use `MultiChildWidget.SetChildren`,
which reconciles by `Key` in C#. `Zigote.UI.FSharp.Gallery/Main.fs` shows both.

## Running it

```sh
dotnet run --project Zigote.UI.FSharp.Gallery
```

The gallery is the reference: counters, todo list with retained rows, derived state, and the rule
commentary above, live. `Zigote.UI.FSharp.Tests` covers the reactive combinators and the `watch`
bridge headlessly.
