# Lists & Scrolling

## Scroll views

`SingleChildScrollView` scrolls one child of any height: wheel, trackpad, touch fling and scrollbar included. Use it for pages, like the one you are reading.

## Long lists

`ListView` virtualises **measure, layout and paint** to the viewport. `ListView.Builder` virtualises *construction* as well: a row is built when it scrolls into view and dropped when it leaves, so a hundred thousand rows cost what a screenful does.

```csharp run height=320
class Demo : ComposedWidget
{
    private const int Count = 100_000;

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return ListView.Builder(
            itemCount: Count,
            itemExtent: 44,
            itemBuilder: i => new Padding(EdgeInsets.Symmetric(horizontal: 16), new Row(spacing: 12, children: [
                new AdwAvatar(size: 28, text: $"Row {i}"),
                new Expanded(new Label($"Row {i:N0}", fontSize: 14)),
                new Label(text: $"#{i % 97:00}", style: AdwTypography.Caption, color: theme.TextMuted),
            ])));
    }
}
```

The trade-off is that **a row that scrolls out is destroyed**, along with its hover state, focus and any nested scroll offset. Keep per-row state in your model, not in the row widget.

## Grids

`GridView.Builder(crossAxisCount, itemCount, builder)` is the grid form, built one row of cells at a time.

```csharp run height=320
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        GridView.Builder(
            crossAxisCount: 6,
            itemCount: 600,
            itemBuilder: i => new DecoratedBox {
                Fill = Color.FromHex(0xFF000000 | (uint)((i * 2654435761u) & 0xFFFFFF)),
                Radius = 10,
                Child = new AspectRatio(1, new Center(new Label($"{i}", fontSize: 13, color: new Color(0xFFFFFFFF)))),
            },
            mainAxisSpacing: 6,
            crossAxisSpacing: 6);
}
```

## Changing items

- **Keep the list in a field** (`private readonly ListView _list = new();`), never inside a `Watch` or a `Build` that re-runs, or its scroll position resets on every rebuild.
- `SetItems(rows, keepScroll: true)` when the new rows refine what was there (a filter narrowing), `false` when it is a different list.
- `HeightOf = i => …` gives rows variable heights; the list keeps a prefix-sum table so scrolling stays O(viewport).
- `AdwColumnView` is the sortable, selectable data table; `AdwTreeView` shows hierarchies; `AdwReorderableList` adds drag-to-reorder.
