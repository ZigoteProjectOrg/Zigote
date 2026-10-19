# Transitions

A transition animates a **change of content**: one child replacing another, a panel growing to fit new content, a page pushed onto a stack, an element flying from a list into a detail view. The aim is continuity. The user should see where the new thing came from and where the old one went.

| Change | Tool |
|---|---|
| Swap one child for another in place | `AnimatedSwitcher` |
| Content grows or shrinks | `AnimatedSize` |
| Push and pop full pages | `Navigator` routes (`MaterialPageRoute`, or your own `Route`) |
| Adwaita page stacks and sidebars | `AdwNavigationView`, `AdwNavigationSplitView`, `AdwViewStack` |
| The same element in two places | `AdwHero` (shared-element flight) |
| Light ↔ dark, accent changes | Built-in theme cross-fade |
| Things appearing and leaving | `.Animate()` presets: `FadeInUp`, `PopIn`, `PopOut`, `FadeOutDown`… |

## Switching and resizing in place

`AnimatedSwitcher` cross-fades whenever you assign a new `Child`. `AnimatedSize` eases its own size towards its child's size, clipping while it moves, which is how disclosure panels open smoothly.

```csharp run height=320
class Demo : ComposedWidget
{
    private readonly AnimatedSwitcher _status = new(child: new Label("Offline", fontSize: 20), duration: 0.25f);
    private readonly AnimatedSize _details = new(child: SizedBox.Shrink(), duration: 0.25f, curve: Curves.EaseInOut);
    private bool _online, _open;

    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        return new Padding(EdgeInsets.All(20), new Column(spacing: 14, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new Row(spacing: 12, children: [
                new AdwButton(label: "Toggle status", onPressed: () =>
                {
                    _online = !_online;
                    _status.Child = _online
                        ? new Label(text: "● Online", style: AdwTypography.Title3, color: p.Success)
                        : new Label(text: "○ Offline", style: AdwTypography.Title3, color: p.Error);
                }),
                _status,
            ]),
            new AdwButton(label: "Show details", onPressed: () =>
            {
                _open = !_open;
                _details.Child = _open
                    ? new DecoratedBox { Fill = p.CardBg, BorderColor = p.CardShade, Radius = 12,
                        Child = new Padding(EdgeInsets.All(16), new Label(
                            "AnimatedSize eased the box open to fit this text, clipping it while it grew. " +
                            "Closing it eases back to nothing. No heights were hard-coded.", fontSize: 14)) }
                    : SizedBox.Shrink();
            }),
            _details,
        ]));
    }
}
```

## Page transitions

`Navigator` keeps a stack of routes and animates between them. `MaterialPageRoute` slides the new page in from the right while fading it up, and reverses on pop. Routes are awaitable: `await context.Push(...)` completes with whatever the page passes to `Pop(result)`.

> **The `context` you're given stays valid.** The `BuildContext` passed to `Build` is anchored to that widget, so the click handlers below can call `context.Push(...)` and `context.Pop()` long after the build: outside the layout walk, lookups follow the widget's real ancestors. Each page here finds the example's own navigator, not the app's.

To make your own transition, subclass `Route<T>` and describe the entering page at progress `t` (0 → 1) with an offset and an opacity. The navigator plays it forwards on push and backwards on pop:

```csharp run height=340
// A custom transition: rise from below while fading in.
sealed class RiseRoute(Widget page) : Route<object?>
{
    public override float TransitionDuration => 0.4f;
    protected override Widget BuildContent(BuildContext context) => page;
    public override Offset TransitionOffset(Size size, float t) => new(0, (1 - Curves.EaseOut(t)) * size.Height * 0.4f);
    public override float TransitionOpacity(float t) => t;
}

sealed class SimplePage(string title, uint color, bool root = false) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        var buttons = new Wrap(spacing: 8, runSpacing: 8, children: [
            new AdwButton(label: "Push (slide)", onPressed: () =>
                context.Push(new MaterialPageRoute(_ => new SimplePage("Slid in", 0xFF2EC27E)))),
            new AdwButton(label: "Push (rise)", onPressed: () =>
                context.Push(new RiseRoute(new SimplePage("Rose up", 0xFFE66100)))),
        ]);
        if (!root) buttons.Children.Add(new AdwButton(label: "Pop", onPressed: () => context.Pop()) { Style = AdwButtonStyle.Suggested });

        return new DecoratedBox {
            Fill = p.ViewBg,
            Child = new Padding(EdgeInsets.All(20), new Column(spacing: 16, crossAxisAlignment: CrossAxisAlignment.Start, children: [
                new DecoratedBox { Fill = new Color(color), Radius = 12,
                    Child = new SizedBox(height: 90, child: new Center(new Label(title, fontSize: 22, color: new Color(0xFFFFFFFF)))) },
                buttons,
            ])),
        };
    }
}

class Demo : ComposedWidget
{
    // A navigator of its own, so pushes stay inside this example.
    protected override Widget Build(BuildContext context) =>
        new Navigator { Home = new SimplePage("Home", 0xFF3584E4, root: true) };
}
```

Other route knobs: `Opaque => false` keeps the page underneath visible (for sheets and dialogs drawn as routes). Esc and the system back gesture call `MaybePop` automatically.

For app-scale navigation, `Navigator.Pages` gives declarative Navigator 2.0 routing: the page list is derived from state, so deep links and back-stack restoration come from the state too.

In Adwaita apps, prefer `AdwNavigationView`. It is a push/pop stack whose header bar title and back button follow the pages, with the GNOME slide transition.

## Shared-element transitions

Wrap the same thing in two places with the same `Tag` and `AdwHero` flies it from one to the other: from the tile you tapped into the dialog it opens, and back again when the dialog closes. Tap a tile:

```csharp run height=300
class Demo : ComposedWidget
{
    private static readonly uint[] Colors = [0xFF3584E4, 0xFF2EC27E, 0xFFE66100, 0xFF9141AC, 0xFFC01C28, 0xFFF5C211];

    private static Widget Swatch(uint color, float radius) =>
        new DecoratedBox { Fill = new Color(color), Radius = radius };

    private static void Open(uint color)
    {
        // The destination needs a real size: a childless box measures to nothing on its own.
        var dialog = new AdwDialog(new Center(new SizedBox(width: 360, height: 260,
            child: new AdwHero(tag: color, child: Swatch(color, 24))))) {
            ContentWidth = 420,
            ContentHeight = 320,
        };
        dialog.Show();
    }

    protected override Widget Build(BuildContext context) =>
        new Center(new Wrap(spacing: 12, runSpacing: 12, children: [
            .. Colors.Select(c => new GestureDetector(
                child: new SizedBox(width: 90, height: 90, child: new AdwHero(tag: c, child: Swatch(c, 14))),
                onTap: () => Open(c))),
        ]));
}
```

The hero already on screen is the source; one mounted later with the same tag is the destination. When the two ends differ (a cropped thumbnail opening into the full picture), set `Flight` on the destination and paint the in-between frames yourself.

## Theme transitions

Assigning a new `Theme` on the app cross-fades the whole window from the old appearance to the new one instead of flashing. `App.ThemeCrossfade` sets the duration (0.2 s by default; 0 swaps instantly). Widgets restyle immediately underneath; only what the eye sees is eased.

## Guidelines

- **Transitions explain, they don't decorate.** Each one should answer "where did this come from?" or "where did that go?".
- Keep them short: 150–300 ms for in-place changes, about 300–400 ms for page transitions.
- Entering motion decelerates (`EaseOut`); leaving motion accelerates (`EaseIn`).
- Hoist what you swap. An `AnimatedSwitcher` swapping between two retained widgets keeps their state; building new ones on every switch resets it.
- Respect reduce-motion: the built-in transitions shorten or skip themselves, and yours should too.
