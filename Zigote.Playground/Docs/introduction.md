# Introduction

Zigote is a **retained-mode UI framework for .NET**, drawn by a native engine written in Zig on top of wgpu. You describe a window as a tree of C# widget objects; the engine turns the paint commands that tree emits into GPU work.

This playground is the documentation. Every page is prose plus **live examples**: the code is real, the preview under it is that code compiled and running inside this app. Change something and press **Run** (or `⌘S` in the editor) to see it.

```csharp run height=160
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new Center(new Label("Hello from a live example!", fontSize: 24));
}
```

## The stack

Each layer depends only on the one below it:

| Layer | Package | What it owns |
|---|---|---|
| Your app | — | Pages, state, business logic |
| Design system | `Zigote.UI.Adwaita` | GNOME Adwaita controls, theme, window chrome |
| Kernel | `Zigote.UI` | Widgets, layout, paint, input, focus, navigation, animation |
| Core | `Zigote.Core` | `Signal`/`Computed`/`Effect`, math, the paint and event ABI |
| Native | `libzigote` (Zig) | SDL3 windows, wgpu rendering, HarfBuzz text, Jolt, flecs, audio |

> Coming from Flutter? The vocabulary will feel familiar — `Column`, `Padding`, `Center`, `InheritedWidget`, `Navigator` — but the model is different: **`Build` runs once**. Widgets are long-lived objects whose fields are their state, and signals push changes into exactly the subtree that reads them. Read *Signals & Watch* before you write your first stateful screen.
