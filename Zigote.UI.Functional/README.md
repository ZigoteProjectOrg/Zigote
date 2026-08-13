# Zigote.UI.Functional

Function → Widget authoring for Zigote.UI: write a component as a plain function instead of a
`ComposedWidget` subclass.

```csharp
static Widget Counter()
{
    var count = new Signal<int>(0);                // state — created once, survives rebuilds
    return new View(ctx => new Row {               // view — re-runs when `count` changes
        Children = {
            new Button("+1", () => count.Value++),
            new Label($"{count.Value}", 17f, ThemeProvider.Of(ctx).OnSurface),
        }
    });
}
```

The function body is the constructor: it runs once, and signals created there are the component's
retained state, held by the closure. The returned `View` is the build. Its builder always runs
inside the measure walk, so:

- **Inherited data is dependable.** `ThemeProvider.Of(ctx)` returns the real theme — never the
  dark fallback an out-of-walk builder sees — and the dependency registers, so a theme flip
  rebuilds the function with the new tokens.
- **Signals just work.** Anything the builder reads schedules a rebuild when it changes, from any
  thread. The rebuild lands in the next walk (a whole-tree measure, not `Watch`'s in-place fast
  path — nest a `Watch` inside a `View` for a hot inner subtree).
- **Lifecycle is one property.** `OnMounted` starts a resource per mount period — a timer, a
  subscription, an `Effect` — and its disposable is torn down on unmount.

Stateful children belong in the function body, not inside the lambda: a child created inside the
builder is recreated (and reset) by every rebuild, while a closed-over one is re-adopted.

## Layout

- `Zigote.UI.Functional` — the library: one widget, `View`.
- `Zigote.UI.Functional.Demo` — an Adwaita window written entirely as functions
  (state, lifecycle, theme reactivity, shape-from-state).
- `Zigote.UI.Functional.Tests` — the `View` contract, headless.

```sh
dotnet test Zigote.UI.Functional.Tests
dotnet run --project Zigote.UI.Functional.Demo
```
