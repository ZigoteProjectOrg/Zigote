# Layout

Layout is a single pass over the tree that runs in two halves:

1. **Measure** (bottom-up). A parent hands each child `Constraints`, a min and max width and height. The child picks a size inside them and returns it.
2. **Layout** (top-down). The parent places each child at an absolute offset, which sets its `Bounds`.

*Constraints go down, sizes come up, the parent sets the position.* If you know Flutter's box protocol, this is the same rule, and the kernel's layout widgets will look familiar.

## Row and Column

`Row` and `Column` lay children out along a main axis. `Expanded` children share whatever space is left over, in proportion to their `flex`. `spacing` puts a fixed gap between children.

```csharp run height=260
static class Box
{
    public static Widget Of(string text, uint argb, float? width = null) =>
        new DecoratedBox {
            Fill = new Color(argb),
            Radius = 8,
            Child = new SizedBox(width: width, height: 56, child: new Center(new Label(text, fontSize: 14, color: new Color(0xFFFFFFFF)))),
        };
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(16), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Row(spacing: 8, children: [
                Box.Of("80", 0xFF3584E4, 80),
                new Expanded(Box.Of("flex 1", 0xFF2EC27E)),
                new Expanded(Box.Of("flex 2", 0xFFE66100), flex: 2),
            ]),
            new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween, children: [
                Box.Of("A", 0xFF9141AC, 60), Box.Of("B", 0xFF9141AC, 60), Box.Of("C", 0xFF9141AC, 60),
            ]),
            new Row(mainAxisAlignment: MainAxisAlignment.Center, spacing: 8, children: [
                Box.Of("centred", 0xFFC01C28, 100), Box.Of("pair", 0xFFC01C28, 100),
            ]),
        ]));
}
```

Try changing `MainAxisAlignment.SpaceBetween` to `SpaceEvenly`, or the `flex: 2` to `flex: 5`.

## The single-child toolkit

| Widget | Does |
|---|---|
| `Padding` | Insets its child by an `EdgeInsets` |
| `Center`, `Align` | Loosens the constraints and positions the child inside its own box |
| `SizedBox` | Forces a width and/or height; `SizedBox.Shrink()` is an empty widget |
| `ConstrainedBox` | Adds min/max constraints on top of the parent's |
| `AspectRatio` | Sizes the child to a width:height ratio |
| `DecoratedBox` | Paints a fill, border, radius and shadow behind its child |
| `ClipRect`, `ClipRRect` | Clips painting and hit testing to its bounds |
| `Opacity`, `Transform` | Paint-time effects that never trigger relayout |

## Stack and Positioned

`Stack` paints its children on top of each other. `Positioned` pins a child to the stack's edges.

```csharp run height=240
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        return new Padding(EdgeInsets.All(16), new Stack(children: [
            new Positioned(left: 0, top: 0, right: 0, bottom: 0,
                child: new DecoratedBox { Fill = AdwPalette.Wash(p.Accent, 0.15f, p.ViewBg), Radius = 16 }),
            new Positioned(left: 16, top: 16,
                child: new Label(text: "Top left", style: AdwTypography.Title3, color: p.Accent)),
            new Positioned(right: 16, bottom: 16,
                child: new AdwButton(label: "Bottom right", onPressed: () => { }) { Pill = true }),
        ]));
    }
}
```

## Responding to the available space

`LayoutBuilder` hands you the constraints at layout time, so a subtree can choose its shape from the space it is actually given. Drag the window narrower and watch this example switch layouts at 500 points.

```csharp run height=200
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new LayoutBuilder((ctx, constraints) =>
        {
            Widget a = new AdwButton(label: "First", onPressed: () => { }) { Pill = true };
            Widget b = new AdwButton(label: "Second", onPressed: () => { }) { Pill = true };
            string note = $"{constraints.MaxWidth:0} pt wide";
            return constraints.MaxWidth > 500
                ? new Center(new Row(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [a, b, new Label(note)]))
                : new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [a, b, new Label(note)]));
        });
}
```

For app-level breakpoints, `AdaptiveBuilder` rebuilds only when the window's size class changes (`Compact` below 600, `Medium` below 840, then `Expanded`). `AdwNavigationSplitView`, the sidebar of this very app, folds into one pane below a width you choose.

> A `Column` inside a vertical scroll view gets an **unbounded** max height. `Expanded` has nothing to divide there, so give such columns `mainAxisSize: MainAxisSize.Min`.
