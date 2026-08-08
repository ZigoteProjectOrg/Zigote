# Zigote.UI.BottomSheet

A draggable, resizable bottom sheet for `Zigote.UI` — the API of pub.dev's
[`bottom_sheet ^4.0.4`](https://pub.dev/packages/bottom_sheet) (`showFlexibleBottomSheet` /
`showStickyFlexibleBottomSheet`), rebuilt on Zigote's retained widget tree and `Navigator`.

It references only `Zigote.UI` + `Zigote.Core`, and it is **design-language agnostic**: the package draws a rounded
card, a scrim and a drag pill, and every colour, radius, shadow and padding comes from a `BottomSheetStyle` that falls
back to the ambient `ThemeData`. Material, libadwaita, or a game HUD each hand in their own tokens —
`Zigote.UI.Adwaita`'s `AdwBottomSheet` is exactly that, this widget in libadwaita's palette.

## Quick start

```csharp
var picked = await BottomSheets.ShowFlexible<string>(
    context,
    (ctx, scroll, sheet) =>
    {
        scroll.Child = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch)
        {
            Children = { /* … rows … */ new Button("Pick", () => sheet.Close("berlin")) },
        };
        return scroll;
    },
    minHeight: 0.3f, initHeight: 0.5f, maxHeight: 0.95f,
    anchors: [0.3f, 0.5f, 0.95f]);
```

Heights are **fractions of the available height** (`bottom_sheet`'s `minHeight` / `initHeight` / `maxHeight`). The task
completes when the sheet closes, carrying whatever was passed to `Close` — or `null` when it was dismissed by the scrim,
a collapse drag, or the system back gesture.

With a pinned header:

```csharp
await BottomSheets.ShowStickyFlexible<object?>(
    context,
    headerBuilder: (ctx, sheet) => new Label("Nearby") { Style = Label.LabelStyle.Title },
    bodyBuilder:   (ctx, scroll, sheet) => scroll,
    minHeaderHeight: 44f, maxHeaderHeight: 96f);
```

## The pieces

| Type                          | What it is                                                                                                 |
|-------------------------------|------------------------------------------------------------------------------------------------------------|
| `BottomSheets`                | The `ShowFlexible` / `ShowStickyFlexible` entry points.                                                    |
| `BottomSheetController`       | The sheet's position. `Extent` is a fraction in `[MinExtent, MaxExtent]`, `Close`, `AnimateTo`, `Anchors`. |
| `FlexibleBottomSheet`         | The widget: scrim + card, drives the extent. Usable standalone as a persistent sheet.                      |
| `FlexibleBottomSheetRoute<T>` | The translucent modal route the shown sheet lives on.                                                      |
| `SheetScrollView`             | The scroller handed to the builder — resizes the sheet before scrolling its content.                       |
| `SheetDragArea`               | A drag surface (the pill, a sticky header, or your own row).                                               |
| `SheetStickyHeader`           | A header whose height follows the sheet.                                                                   |
| `BottomSheetStyle`            | Colour / radius / shadow / handle tokens, theme-resolved when null.                                        |

## Reacting to the sheet moving

`bottom_sheet` re-runs its builder with a `bottomSheetOffset` on every frame of a drag. A retained tree has no such
callback, so `BottomSheetController.Extent` is a `Signal<float>` instead — wrap the part that has to react in a `Watch`
and everything else stays put:

```csharp
new Watch(() => new Label(sheet.Extent.Value > 0.8f ? "Nearby places" : "Nearby"))
```

`ExtentChanged` and `Settling` are the imperative equivalents (`Settling` fires when a released drag *decides* where it
is going, not when the animation lands).

## The scroll hand-off

Dragging up grows the sheet until it is fully expanded and only then scrolls the content; dragging down scrolls the
content back to its top and only then shrinks the sheet — which is what makes a sheet feel like one surface. It only
works if the builder actually uses the `SheetScrollView` it was handed, exactly like attaching `bottom_sheet`'s
`ScrollController`.

**Known limit.** A drag that starts *inside the content* can shrink the sheet only down to `minHeight`; collapsing to
dismiss lives on the drag handle, a sticky header, the scrim, and the system back action. Zigote picks one scroll target
per touch gesture and only delivers the release to it when it is also the pressed widget, so a slow release in the body
has no settle event — and a sheet must never be strandable below its minimum. A downward *flick* in the body still
dismisses.

## Not carried over from `bottom_sheet`

`useRootNavigator` / `useRootScaffold` (push onto whichever `Navigator` the context resolves, or push a
`FlexibleBottomSheetRoute<T>` yourself), `keyboardBarrierColor` (the sheet simply sits above the keyboard —
`MediaQuery.ViewInsets`), and the `decoration` grab-bag, which `BottomSheetStyle` replaces.
