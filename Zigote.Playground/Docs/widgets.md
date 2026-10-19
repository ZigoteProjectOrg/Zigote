# Widgets

Everything on screen is a `Widget`, a plain C# object that knows how to measure, lay out, paint and handle input. You build a tree of them, and the tree is **retained**: it stays alive across frames, and you change it by mutating it.

There are three kinds you write or use every day:

| Kind | Base class | Use it for |
|---|---|---|
| Composed | `ComposedWidget` | Almost everything you write: a `Build` method that returns other widgets |
| Leaf | `LeafWidget` | Primitives that draw themselves: text, icons, canvases |
| Container | `Widget` with children | Layout: `Row`, `Column`, `Stack`, `Padding`… |

## Composing

A `ComposedWidget` overrides one method. **`Build` runs once**, the first time the widget is measured, and its result is cached. That one rule explains most of what is different from Flutter, Compose or SwiftUI.

```csharp run height=220
sealed class ProfileCard(string name, string role) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new Row(spacing: 12, mainAxisSize: MainAxisSize.Min, children: [
            new AdwAvatar(size: 48, text: name),
            new Column(crossAxisAlignment: CrossAxisAlignment.Start, mainAxisSize: MainAxisSize.Min, children: [
                new Label(text: name, style: AdwTypography.Heading),
                new Label(text: role, style: AdwTypography.Caption, color: theme.TextSecondary),
            ]),
        ]);
    }
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new Center(new Column(spacing: 16, mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new ProfileCard("Ada Lovelace", "Analytical engines"),
            new ProfileCard("Grace Hopper", "Compilers"),
            new ProfileCard("Alan Turing", "Computability"),
        ]));
}
```

## Fields are state

Because the tree is retained, a widget's fields **are** its state. Hover, focus, scroll offset and running animations live on the instances. To change what is shown, mutate the instance you kept, then say what you invalidated:

| Call | Rebuild | Relayout | Repaint | Use for |
|---|---|---|---|---|
| `MarkNeedsPaint()` | — | — | ✓ | Visual-only change, size unchanged |
| `MarkNeedsLayout()` | — | ✓ | ✓ | **The default.** Text, size or visibility changed |
| `MarkNeedsBuild()` | ✓ | ✓ | ✓ | The child *structure* must change |

```csharp run height=160
class Demo : ComposedWidget
{
    // Kept in a field: the same Label instance lives for the life of the page.
    private readonly Label _label = new("Clicked 0 times", fontSize: 18);
    private int _clicks;

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            _label,
            new AdwButton(label: "Click", onPressed: () =>
            {
                _label.Text = $"Clicked {++_clicks} times";
                MarkNeedsLayout();   // Build does not run again; this re-measures and repaints.
            }),
        ]));
}
```

Most of the time you will not mutate by hand. Put the state in a `Signal` and let a `Watch` swap the subtree that reads it (see *Signals & Watch*). Mutation is what makes retained mode cheap; signals are what make it pleasant.

> **Never recreate a widget you can mutate.** A new instance loses its hover, focus, scroll position and animations, and costs a real allocation for the life of the screen.

## Keys

When you replace a whole list of children with `SetChildren`, give each child a `Key`. The reconciler then reuses the existing instances across inserts, removals and reorders instead of matching them by position.

```csharp
row.SetChildren(items.Select(i => new ItemTile(i) { Key = new ValueKey<int>(i.Id) }));
```
