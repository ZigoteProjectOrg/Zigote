# Zigote.UI.HelloWorld

The Zigote counterpart of Flutter's counter app: one file, one screen, one piece of state.

```sh
dotnet run --project Zigote.UI.HelloWorld
```

## What it shows

```csharp
private readonly Signal<int> _count = new(0);

new Watch(() => new Text(_count.Value.ToString()))   // rebuilds only this Text
...
new AdwButton("Increment", () => _count.Value++)
```

- **`AdwaitaApp`** boots the engine, opens the window, installs the theme; `Run()` drives the frame loop.
- **`AdwToolbarView` / `AdwHeaderBar` / `AdwButton`** are the Adwaita shell: content under a header bar that hosts the window controls.
- **`Signal<T>`** is the state. **`Watch`** is the bridge to the widget tree: it runs its builder under dependency
  tracking and rebuilds only the subtree that read the signal — so the button re-runs one `Text`, not the page.
- The state lives in the signal, so the page just composes. There is no stateless/stateful split: a widget's fields are
  its state, and `OwnEffect`/`Watch` connect signals to them.

## DevTools

Press **Shift+D** in the running app for the debug overlay — see
[`Zigote.UI.DevTools`](../Zigote.UI.DevTools/README.md) for what each tab shows.

```csharp
protected override void OnInit()
{
    base.OnInit();
    DevTools.Install(App!, DevToolsProfile.TwoD);   // General + 2D·UI tabs
}
```

Referencing the `Zigote.UI.DevTools` project is by itself enough — the host auto-installs the overlay with the `Auto`
profile. The explicit call is only to pin the profile (`TwoD` drops the 3D renderer tab a UI app has nothing to put in).

Worth doing once with this app open: increment the counter and watch **2D·UI → `ui.watch_rebuilds`**
go up by exactly one per press. That is the reactive claim made measurable — the page is not rebuilding, one `Text` is.

## Next

- [`Zigote.UI.Adwaita.Gallery`](../Zigote.UI.Adwaita.Gallery/README.md) — every widget, an adaptive shell, multi-window.
- [`docs/migration/`](../docs/migration/README.md) — coming from Flutter, Compose, SwiftUI or WPF.
